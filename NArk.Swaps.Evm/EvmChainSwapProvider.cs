using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Fees;
using NArk.Core.Helpers;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Evm.Models;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;

namespace NArk.Swaps.Evm;

/// <summary>
/// <see cref="ISwapProvider"/> for the <c>Ark &lt;-&gt; EvmArbitrum</c> Chain Swap route,
/// backed by Nethereum calls to Boltz's deployed <c>ERC20Swap</c> contract on Arbitrum.
/// Reuses <see cref="BoltzClient"/>'s generic <c>v2/swap/chain</c> REST client (create/status)
/// as-is; the EVM-specific lock/claim/refund mechanics live in <see cref="EvmChainClient"/>.
/// Claim/lock are non-cooperative (script-path) only — see the plan's deferred-scope section
/// for EIP-712 cooperative signing. The Ark-side refund (<c>ChainArkToEvm</c>) mirrors
/// <c>BoltzSwapProvider.Refunds.cs</c>'s <c>ChainArkToBtc</c> path exactly: cooperative refund
/// first (Boltz co-signs), falling back to the refund-without-receiver batch-intent path once
/// <see cref="VHTLCContract.RefundLocktime"/> elapses.
/// </summary>
public class EvmChainSwapProvider : ISwapProvider
{
    public const string Id = "boltz-evm";

    private readonly BoltzClient _boltzClient;
    private readonly IClientTransport _clientTransport;
    private readonly IWalletProvider _walletProvider;
    private readonly ISwapStorage _swapStorage;
    private readonly IContractService _contractService;
    private readonly IContractStorage _contractStorage;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly ISafetyService _safetyService;
    private readonly IIntentStorage _intentStorage;
    private readonly IBitcoinBlockchain _chainTimeProvider;
    private readonly IIntentGenerationService? _intentGenerationService;
    private readonly EvmSwapOptions _options;
    private readonly ILogger<EvmChainSwapProvider>? _logger;
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _pollingTask;
    private EvmChainClient? _evmChainClient;
    private readonly SemaphoreSlim _evmClientInitLock = new(1, 1);

    /// <summary>Maps refund intent txId → swapId so <see cref="OnRefundIntentChanged"/> can
    /// trigger an immediate poll when the batch session for a refund-without-receiver intent
    /// completes, instead of waiting for the next routine poll tick.</summary>
    private readonly ConcurrentDictionary<string, string> _intentToSwapId = new();

    // ─── WebSocket (real-time status push, mirroring BoltzSwapProvider — the REST poll
    // loop above stays as the periodic safety net, same dual-mechanism approach used
    // throughout this codebase, e.g. VtxoSynchronizationService's stream+routine-poll). ───

    /// <summary>Swap ids currently subscribed on the persistent websocket, kept in sync with
    /// the active set by <see cref="RunPollLoopAsync"/>'s per-tick diff.</summary>
    private readonly ConcurrentDictionary<string, byte> _swapsIdToWatch = new();
    private readonly Channel<string> _wsTriggerChannel = Channel.CreateUnbounded<string>();
    private Task? _websocketTask;
    private Task? _wsTriggerReaderTask;
    private BoltzWebsocketClient? _websocket;
    private readonly SemaphoreSlim _websocketLock = new(1, 1);

    public EvmChainSwapProvider(
        BoltzClient boltzClient,
        IClientTransport clientTransport,
        IWalletProvider walletProvider,
        ISwapStorage swapStorage,
        IContractService contractService,
        IContractStorage contractStorage,
        IVtxoStorage vtxoStorage,
        ISafetyService safetyService,
        IIntentStorage intentStorage,
        IBitcoinBlockchain chainTimeProvider,
        IOptions<EvmSwapOptions> options,
        IIntentGenerationService? intentGenerationService = null,
        ILogger<EvmChainSwapProvider>? logger = null)
    {
        _boltzClient = boltzClient;
        _clientTransport = clientTransport;
        _walletProvider = walletProvider;
        _swapStorage = swapStorage;
        _contractService = contractService;
        _contractStorage = contractStorage;
        _vtxoStorage = vtxoStorage;
        _safetyService = safetyService;
        _intentStorage = intentStorage;
        _chainTimeProvider = chainTimeProvider;
        _intentGenerationService = intentGenerationService;
        _options = options.Value;
        _logger = logger;
        _transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(
            clientTransport, safetyService, walletProvider, intentStorage);
    }

    public string ProviderId => Id;
    public string DisplayName => "Boltz (EVM)";

    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    // ─── Routes ─────────────────────────────────────────────────────────────

    public bool SupportsRoute(SwapRoute route) => route switch
    {
        { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.EvmArbitrum } => true,
        { Source.Network: SwapNetwork.EvmArbitrum, Destination.Network: SwapNetwork.Ark } => true,
        _ => false
    };

    public Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<SwapRoute>>([
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.ArbitrumTbtc),
            new SwapRoute(SwapAsset.ArbitrumTbtc, SwapAsset.ArkBtc),
        ]);

    // ─── Limits / quotes ────────────────────────────────────────────────────

    public async Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct)
    {
        var pairs = await _boltzClient.GetFromJsonAsync<Dictionary<string, Dictionary<string, EvmChainPairDetails>>>(
            "v2/swap/chain", ct) ?? throw new InvalidOperationException("Boltz returned no chain-swap pairs.");

        var (fromKey, toKey) = route.Source.Network == SwapNetwork.EvmArbitrum
            ? (_options.PairCurrency, "ARK")
            : ("ARK", _options.PairCurrency);

        if (!pairs.TryGetValue(fromKey, out var toMap) || !toMap.TryGetValue(toKey, out var details))
            throw new InvalidOperationException($"Boltz has no chain-swap pair {fromKey} -> {toKey}.");

        return new SwapLimits
        {
            Route = route,
            MinAmount = details.Limits.Minimal,
            MaxAmount = details.Limits.Maximal,
            FeePercentage = details.Fees.Percentage,
            MinerFee = details.Fees.MinerFees.Server
        };
    }

    public async Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct)
    {
        var limits = await GetLimitsAsync(route, ct);
        var fee = (long)(amount * limits.FeePercentage) + limits.MinerFee;
        return new SwapQuote
        {
            Route = route,
            SourceAmount = amount,
            DestinationAmount = amount - fee,
            TotalFees = fee,
            ExchangeRate = 1m
        };
    }

    // ─── Swap creation ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates an ARK -&gt; EvmArbitrum chain swap: we lock an Ark VHTLC, Boltz locks tBTC on
    /// Arbitrum for <paramref name="evmClaimAddress"/> (our own EVM account) to claim.
    /// Persists the swap and imports the VHTLC contract before returning, so the poll loop
    /// picks it up on the next tick.
    /// </summary>
    public async Task<EvmChainSwapResult> CreateArkToEvmSwapAsync(
        string walletId, long amountSats, OutputDescriptor refundDescriptor, string evmClaimAddress,
        byte[]? preimage = null, CancellationToken ct = default)
    {
        var operatorTerms = await _clientTransport.GetServerInfoAsync(ct);
        var extracted = refundDescriptor.Extract();
        var refundPubKeyHex = Convert.ToHexString(
            extracted.PubKey?.ToBytes() ?? extracted.XOnlyPubKey.ToBytes()).ToLowerInvariant();

        preimage ??= RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);
        var ephemeralEvmKey = EthECKey.GenerateKey();

        var request = new EvmChainCreateRequest
        {
            From = "ARK",
            To = _options.PairCurrency,
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            RefundPublicKey = refundPubKeyHex,
            ClaimAddress = evmClaimAddress,
            UserLockAmount = amountSats,
            ReferralId = _boltzClient.ReferralId,
        };

        var response = await _boltzClient.PostAsJsonAsync<EvmChainCreateRequest, ChainResponse>(
            "v2/swap/chain", request, ct);

        var lockupDetails = response.LockupDetails
            ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing lockup details (Ark side).");
        var timeouts = lockupDetails.Timeouts ?? lockupDetails.TimeoutBlockHeights
            ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing timeouts in Ark lockup details.");

        var receiverDescriptor = ParseOutputDescriptor(
            lockupDetails.ServerPublicKey
                ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing serverPublicKey."),
            operatorTerms.Network);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: refundDescriptor,
            receiver: receiverDescriptor,
            preimage: preimage,
            refundLocktime: new LockTime(timeouts.Refund),
            unilateralClaimDelay: ParseSequence(timeouts.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(timeouts.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(timeouts.UnilateralRefundWithoutReceiver));

        var computedAddress = vhtlcContract.GetArkAddress()
            .ToString(operatorTerms.Network.ChainName == ChainName.Mainnet);
        if (computedAddress != lockupDetails.LockupAddress)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: Ark lockup address mismatch. " +
                $"Computed {computedAddress}, Boltz expects {lockupDetails.LockupAddress}");

        var result = new EvmChainSwapResult(response, preimage, preimageHash, ephemeralEvmKey, vhtlcContract);
        await PersistSwapAsync(walletId, result, ArkSwapType.ChainArkToEvm,
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.ArbitrumTbtc), amountSats, operatorTerms.Network, ct);
        return result;
    }

    /// <summary>
    /// Creates an EvmArbitrum -&gt; ARK chain swap: we lock tBTC in <c>ERC20Swap</c> on
    /// Arbitrum ourselves, Boltz locks an Ark VHTLC for <paramref name="claimDescriptor"/>
    /// (our Ark receiving descriptor) to claim. Persists the swap and imports the VHTLC
    /// contract before returning, so the poll loop picks it up on the next tick.
    /// </summary>
    public async Task<EvmChainSwapResult> CreateEvmToArkSwapAsync(
        string walletId, long amountSats, OutputDescriptor claimDescriptor,
        byte[]? preimage = null, CancellationToken ct = default)
    {
        var operatorTerms = await _clientTransport.GetServerInfoAsync(ct);
        var extractedClaim = claimDescriptor.Extract();
        var claimPubKeyHex = Convert.ToHexString(
            extractedClaim.PubKey?.ToBytes() ?? extractedClaim.XOnlyPubKey.ToBytes()).ToLowerInvariant();

        preimage ??= RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);
        var ephemeralEvmKey = EthECKey.GenerateKey();

        var request = new ChainRequest
        {
            From = _options.PairCurrency,
            To = "ARK",
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            ClaimPublicKey = claimPubKeyHex,
            ServerLockAmount = amountSats,
            ReferralId = _boltzClient.ReferralId,
        };

        var response = await _boltzClient.CreateChainSwapAsync(request, ct);

        var claimDetails = response.ClaimDetails
            ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing claim details (Ark side).");
        var timeouts = claimDetails.TimeoutBlockHeights ?? claimDetails.Timeouts
            ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing timeouts in Ark claim details.");

        var senderDescriptor = ParseOutputDescriptor(
            claimDetails.ServerPublicKey
                ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing serverPublicKey."),
            operatorTerms.Network);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: senderDescriptor,
            receiver: claimDescriptor,
            preimage: preimage,
            refundLocktime: new LockTime(timeouts.Refund),
            unilateralClaimDelay: ParseSequence(timeouts.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(timeouts.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(timeouts.UnilateralRefundWithoutReceiver));

        var computedAddress = vhtlcContract.GetArkAddress()
            .ToString(operatorTerms.Network.ChainName == ChainName.Mainnet);
        if (computedAddress != claimDetails.LockupAddress)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: Ark claim address mismatch. " +
                $"Computed {computedAddress}, Boltz expects {claimDetails.LockupAddress}");

        var result = new EvmChainSwapResult(response, preimage, preimageHash, ephemeralEvmKey, vhtlcContract);
        await PersistSwapAsync(walletId, result, ArkSwapType.ChainEvmToArk,
            new SwapRoute(SwapAsset.ArbitrumTbtc, SwapAsset.ArkBtc), amountSats, operatorTerms.Network, ct);
        return result;
    }

    /// <summary>
    /// Persists the swap record and imports the Ark VHTLC contract, mirroring
    /// <c>SwapsManagementService</c>'s pattern for the BTC leg — without this, the swap is
    /// never tracked and the poll loop never sees it.
    /// </summary>
    private async Task PersistSwapAsync(
        string walletId, EvmChainSwapResult result, ArkSwapType swapType, SwapRoute route,
        long expectedAmountSats, NBitcoin.Network network, CancellationToken ct)
    {
        var contract = result.Contract!;
        await _contractService.ImportContract(walletId, contract,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"swap:{result.Swap.Id}" },
            cancellationToken: ct);

        var arkAddress = contract.GetArkAddress();
        var swap = new ArkSwap(
            result.Swap.Id,
            walletId,
            swapType,
            "",
            expectedAmountSats,
            arkAddress.ScriptPubKey.ToHex(),
            arkAddress.ToString(network.ChainName == ChainName.Mainnet),
            ArkSwapStatus.Pending,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Convert.ToHexString(result.PreimageHash).ToLowerInvariant())
        {
            Metadata = new Dictionary<string, string>
            {
                [SwapMetadata.Preimage] = Convert.ToHexString(result.Preimage).ToLowerInvariant(),
                [SwapMetadata.BoltzResponse] = JsonSerializer.Serialize(result.Swap),
            },
            ProviderId = Id,
            Route = route,
        };

        await _swapStorage.SaveSwap(walletId, swap, ct);
    }

    // ─── Lifecycle: websocket push (primary) + REST poll loop (safety net) ─────────

    public Task StartAsync(CancellationToken ct)
    {
        _pollingTask = RunPollLoopAsync(_shutdownCts.Token);
        _websocketTask = RunWebsocketLoop(_shutdownCts.Token);
        _wsTriggerReaderTask = RunWsTriggerReaderAsync(_shutdownCts.Token);
        _intentStorage.IntentChanged += OnRefundIntentChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _intentStorage.IntentChanged -= OnRefundIntentChanged;
        _shutdownCts.Cancel();
        _wsTriggerChannel.Writer.TryComplete();
        await Drain(_pollingTask);
        await Drain(_websocketTask);
        await Drain(_wsTriggerReaderTask);
    }

    /// <summary>
    /// Awaits a background task, swallowing any exception. Once shutdown has been requested,
    /// a fault from work that was interrupted mid-flight (e.g. a refund's nested await noticing
    /// the now-cancelled <see cref="_shutdownCts"/> token deeper in its call chain than the
    /// per-tick try/catch in <see cref="RunPollLoopAsync"/> covers) is an artifact of the
    /// cancellation itself, not a real symptom — mirrors <c>BoltzSwapProvider.Lifecycle.cs</c>'s
    /// own <c>Drain</c> helper.
    /// </summary>
    private static async Task Drain(Task? task)
    {
        if (task is null) return;
        try { await task; }
        catch { /* expected on cancel */ }
    }

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var swaps = await _swapStorage.GetSwaps(
                    swapTypes: [ArkSwapType.ChainArkToEvm, ArkSwapType.ChainEvmToArk],
                    active: true,
                    cancellationToken: ct);
                var ourSwaps = swaps.Where(s => s.ProviderId == Id).ToList();

                // Keep the persistent websocket's subscriptions in sync with the active set.
                // Covers both "new swap since the websocket last (re)connected" and the initial
                // race against RunWebsocketLoop's own startup snapshot — self-heals within one
                // tick either way, no separate seeding needed.
                var newlyActive = ourSwaps
                    .Select(s => s.SwapId)
                    .Where(id => _swapsIdToWatch.TryAdd(id, 0))
                    .ToArray();
                if (newlyActive.Length > 0)
                    await SubscribeOnWebsocketAsync(newlyActive, ct);

                foreach (var swap in ourSwaps)
                {
                    await PollSwapAsync(swap, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger?.LogError(ex, "EVM swap poll loop iteration failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }

    private async Task PollSwapAsync(ArkSwap swap, CancellationToken ct)
    {
        var status = await _boltzClient.GetSwapStatusAsync(swap.SwapId, ct);
        if (status == null)
            return;

        var action = EvmChainOperationClassifier.Classify(swap, status.Status);
        switch (action)
        {
            case EvmSwapAction.CanClaimEvmLockup:
                await TryClaimEvmLockupAsync(swap, ct);
                break;
            case EvmSwapAction.CanRefundEvmLockup:
                await TryRefundEvmLockupAsync(swap, ct);
                break;
            case EvmSwapAction.CanClaimArkLockup:
                // No explicit action: PersistSwapAsync already imported this VHTLC via
                // IContractService.ImportContract(AwaitingFundsBeforeDeactivate), which puts its
                // script in VtxoSynchronizationService's watched set. Once the VTXO lands, the
                // wallet-wide SweeperService (always running — registered unconditionally by
                // AddArkCoreServices) claims it via SwapSweepPolicy/VHTLCContractTransformer,
                // exactly like BoltzSwapProvider's ChainBtcToArk direction already does — see
                // InitiateBtcToArkChainSwap's "Import VHTLC contract for sweeper to claim" comment.
                break;
            case EvmSwapAction.CanRefundArkLockup:
                await TryCoopRefundArkToEvm(swap, ct);
                return;
        }

        var terminal = BoltzSwapStatus.ToArkSwapStatus(status.Status);
        if (terminal != null && terminal != swap.Status)
        {
            await _swapStorage.UpdateSwapStatus(swap.WalletId, swap.SwapId, terminal.Value, status.FailureReason, ct);
            SwapStatusChanged?.Invoke(this,
                new SwapStatusChangedEvent(swap.SwapId, swap.WalletId, Id, terminal.Value, status.FailureReason));

            if (terminal.Value.IsTerminalState())
            {
                _swapsIdToWatch.TryRemove(swap.SwapId, out _);
                await UnsubscribeOnWebsocketAsync([swap.SwapId], ct);
            }
        }
    }

    private async Task TryClaimEvmLockupAsync(ArkSwap swap, CancellationToken ct)
    {
        var preimageHex = swap.Get(SwapMetadata.Preimage)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: missing preimage in metadata.");
        var preimage = Convert.FromHexString(preimageHex);

        var client = await GetEvmChainClientAsync(ct);
        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];

        // Amount/refundAddress/timelock come from Boltz's Lockup event, not our own records —
        // Boltz is the one who locked this side of the swap.
        var lockup = await client.FindLockupEventAsync(Hashes.SHA256(preimage), ct)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: no Lockup event found yet.");

        await client.ClaimAsync(preimage, lockup.Amount, tokenAddress, lockup.RefundAddress, lockup.Timelock, ct);
        _logger?.LogInformation("Swap {SwapId}: claimed EVM lockup", swap.SwapId);
    }

    private async Task TryRefundEvmLockupAsync(ArkSwap swap, CancellationToken ct)
    {
        var preimageHex = swap.Get(SwapMetadata.Preimage)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: missing preimage in metadata.");
        var preimageHash = Hashes.SHA256(Convert.FromHexString(preimageHex));

        var client = await GetEvmChainClientAsync(ct);
        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];

        var lockup = await client.FindLockupEventAsync(preimageHash, ct)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: no Lockup event found to refund.");

        await client.RefundAsync(preimageHash, lockup.Amount, tokenAddress, lockup.ClaimAddress, lockup.Timelock, ct);
        _logger?.LogInformation("Swap {SwapId}: refunded EVM lockup", swap.SwapId);
    }

    // ─── Ark-side refund (ChainArkToEvm) ────────────────────────────────────
    // Mirrors NArk.Swaps.Boltz.BoltzSwapProvider.Refunds.cs's ChainArkToBtc path exactly:
    // cooperative refund first (Boltz co-signs via the same generic
    // POST /v2/swap/chain/{id}/refund/ark endpoint that path uses — it's keyed only by
    // swapId, not scoped to any particular "to" currency), falling back to the
    // refund-without-receiver batch-intent path once RefundLocktime elapses (arkd's
    // checkpoint endpoint rejects that script's absolute-CLTV directly via a normal
    // SpendingService.Spend, so it has to go through IIntentGenerationService instead).

    private async Task TryCoopRefundArkToEvm(ArkSwap swap, CancellationToken ct)
    {
        _logger?.LogInformation(
            "Swap {SwapId}: chain swap expired (ChainArkToEvm), attempting cooperative refund", swap.SwapId);

        // A refund-without-receiver batch may already be in flight (or settled) from a
        // previous poll. Resolve that first: once the batch settles the lockup VTXO is
        // spent, and without this check the coop attempt below would see "no lockup" and
        // incorrectly mark the swap Failed.
        var refundIntentStatus = await CheckRefundWithoutReceiverIntentAsync(swap, ct);
        if (refundIntentStatus is not null) return;

        if (await CoopRefundArkToEvmChainSwap(swap, ct)) return;

        // Nothing to recover — mark Failed so the poll stops retrying.
        var vtxosLocked = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: ct);
        if (vtxosLocked.Count == 0 && swap.Status != ArkSwapStatus.Failed)
        {
            _logger?.LogInformation("Swap {SwapId}: expired with no observable lockup — marking Failed", swap.SwapId);
            await MarkSwapTerminalAsync(swap, ArkSwapStatus.Failed, "Swap expired before any funds were locked", ct);
        }
    }

    private async Task<bool> CoopRefundArkToEvmChainSwap(ArkSwap swap, CancellationToken ct)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToEvm) return false;
        if (swap.Status == ArkSwapStatus.Refunded) return true;

        ArkServerInfo? serverInfo = null;
        VHTLCContract? contract = null;
        ArkVtxo? vtxo = null;
        IDestination? refundDestination = null;
        try
        {
            serverInfo = await _clientTransport.GetServerInfoAsync(ct);

            var matchedSwapContracts = await _contractStorage.GetContracts(
                walletIds: [swap.WalletId], scripts: [swap.ContractScript], cancellationToken: ct);
            var matchedSwapContractEntity = matchedSwapContracts.SingleOrDefault(e => e.Type == VHTLCContract.ContractType);
            if (matchedSwapContractEntity is null)
            {
                _logger?.LogWarning("Swap {SwapId}: VHTLC contract row not found for Ark refund", swap.SwapId);
                return false;
            }
            contract = ArkContractParser.Parse(matchedSwapContractEntity.Type, matchedSwapContractEntity.AdditionalData,
                serverInfo.Network) as VHTLCContract;
            if (contract is null)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to parse VHTLC contract for Ark refund", swap.SwapId);
                return false;
            }

            // Same arkd refresh pattern BoltzSwapProvider.Refunds.cs uses — closes the gap
            // between the indexer subscription stream and what arkd actually has right now.
            await foreach (var freshVtxo in _clientTransport.GetVtxoByScriptsAsSnapshot(
                               new HashSet<string> { swap.ContractScript }, ct))
            {
                await _vtxoStorage.UpsertVtxo(freshVtxo, ct);
            }

            var vtxos = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: ct);
            if (vtxos.Count == 0)
            {
                _logger?.LogWarning("Swap {SwapId}: no VTXOs at VHTLC script for Ark refund", swap.SwapId);
                return false;
            }

            vtxo = vtxos.FirstOrDefault(v => (long)v.Amount == swap.ExpectedAmount && !v.IsSpent());
            if (vtxo is null)
            {
                _logger?.LogWarning(
                    "Swap {SwapId}: no unspent VTXO of expected amount {ExpectedAmount} at swap script (have {Total})",
                    swap.SwapId, swap.ExpectedAmount, vtxos.Count);
                return false;
            }

            var timeHeight = await _chainTimeProvider.GetChainTime(ct);
            if (!vtxo.CanSpendOffchain(timeHeight))
            {
                _logger?.LogDebug("Swap {SwapId}: VHTLC VTXO not spendable offchain (spent/swept/expired)", swap.SwapId);
                return false;
            }

            (refundDestination, swap) = await swap.GetOrDeriveRefundDestinationAsync(
                _contractService, _swapStorage, serverInfo.Network, ct);

            var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

            var (arkTx, checkpoints) = await _transactionBuilder.ConstructArkTransaction(
                [arkCoin], [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundDestination)], serverInfo, ct);

            if (checkpoints.Count != 1)
                throw new InvalidOperationException(
                    $"Swap {swap.SwapId}: expected exactly 1 checkpoint for a single-input Ark refund, " +
                    $"got {checkpoints.Count}. Protocol invariant violated or SDK out of sync.");
            var checkpoint = checkpoints.First();

            var refundResponse = await _boltzClient.RefundChainSwapArkAsync(swap.SwapId,
                new ChainArkRefundRequest { Transaction = arkTx.ToBase64(), Checkpoint = checkpoint.Psbt.ToBase64() }, ct);

            var boltzSignedRefundPsbt = PSBT.Parse(refundResponse.Transaction, serverInfo.Network);
            var boltzSignedCheckpointPsbt = PSBT.Parse(refundResponse.Checkpoint, serverInfo.Network);
            arkTx.UpdateFrom(boltzSignedRefundPsbt);
            checkpoint.Psbt.UpdateFrom(boltzSignedCheckpointPsbt);

            await _transactionBuilder.SubmitArkTransaction([arkCoin], arkTx, [checkpoint], ct);

            await MarkSwapTerminalAsync(swap, ArkSwapStatus.Refunded, null, ct);
            _logger?.LogInformation("Swap {SwapId}: ARK->EVM cooperative refund completed", swap.SwapId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: ARK->EVM cooperative refund failed", swap.SwapId);
            if (contract is not null && vtxo is not null && refundDestination is not null && serverInfo is not null)
                return await TryRefundWithoutReceiverAsync(swap, contract, vtxo, refundDestination, serverInfo, ct);
            return false;
        }
    }

    /// <summary>
    /// Fallback for when Boltz permanently refuses the cooperative co-sign: submits the VHTLC
    /// spend via the <c>refundWithoutReceiver</c> tapscript (server + sender, absolute CLTV) as
    /// an Arkade batch intent once <see cref="VHTLCContract.RefundLocktime"/> has elapsed.
    /// The batch path is required because arkd's checkpoint (<c>SubmitTx</c>) endpoint rejects
    /// this closure's block-height CLTV directly (<c>blockTypeAllowed=false</c>); the
    /// batch/<c>JoinRound</c> path sets <c>blockTypeAllowed=true</c> and enforces the locktime
    /// via the forfeit tx's <c>nLockTime</c> instead.
    /// </summary>
    private async Task<bool> TryRefundWithoutReceiverAsync(
        ArkSwap swap, VHTLCContract contract, ArkVtxo vtxo, IDestination refundDestination,
        ArkServerInfo serverInfo, CancellationToken ct)
    {
        var timeHeight = await _chainTimeProvider.GetChainTime(ct);

        var elapsed = contract.RefundLocktime.IsTimeLock
            ? contract.RefundLocktime.Date <= timeHeight.Timestamp
            : (uint)timeHeight.Height >= contract.RefundLocktime.Value;

        if (!elapsed)
        {
            _logger?.LogDebug("Swap {SwapId}: RefundLocktime {Locktime} not yet elapsed — retrying coop on next poll",
                swap.SwapId, contract.RefundLocktime.Value);
            return false;
        }

        // If we already submitted a refund intent, check its state before creating another.
        var intentStatus = await CheckRefundWithoutReceiverIntentAsync(swap, ct);
        if (intentStatus is not null) return intentStatus.Value;

        if (_intentGenerationService is null)
        {
            _logger?.LogError(
                "Swap {SwapId}: cannot generate refund intent — no IIntentGenerationService registered", swap.SwapId);
            return false;
        }

        try
        {
            _logger?.LogInformation(
                "Swap {SwapId}: RefundLocktime elapsed, submitting refund-without-receiver batch intent", swap.SwapId);

            var arkCoin = new ArkCoin(swap.WalletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
                vtxo.OutPoint, vtxo.TxOut, contract.Sender,
                contract.CreateRefundWithoutReceiverScript(), null, contract.RefundLocktime, null,
                vtxo.Swept, vtxo.Unrolled);

            // Estimate fee against the full input amount, then deduct to get the net output.
            var feeEstimator = new DefaultFeeEstimator(_clientTransport, _chainTimeProvider);
            var fee = await feeEstimator.EstimateFeeAsync(
                [arkCoin], [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundDestination)], ct);
            var netOutput = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(arkCoin.Amount.Satoshi - fee), refundDestination);

            var spec = new ArkIntentSpec([arkCoin], [netOutput], DateTimeOffset.UtcNow, null);
            var intentTxId = await _intentGenerationService.GenerateManualIntent(swap.WalletId, spec, ct);
            _intentToSwapId[intentTxId] = swap.SwapId;

            var updatedSwap = swap with
            {
                Metadata = new Dictionary<string, string>(swap.Metadata ?? []) { [SwapMetadata.RefundIntentTxId] = intentTxId },
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _swapStorage.SaveSwap(swap.WalletId, updatedSwap, ct);
            _logger?.LogInformation(
                "Swap {SwapId}: refund intent {IntentTxId} submitted — waiting for Arkade batch settlement",
                swap.SwapId, intentTxId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: refund-without-receiver failed", swap.SwapId);
            return false;
        }
    }

    /// <summary>
    /// Inspects the in-flight refund-without-receiver batch intent (if any) recorded in
    /// <see cref="SwapMetadata.RefundIntentTxId"/> and reports what the caller should do:
    /// <c>true</c> — the batch settled, the swap is now <see cref="ArkSwapStatus.Refunded"/>;
    /// <c>false</c> — an intent is still in flight, the caller should wait and not re-attempt
    /// the cooperative refund or mark the swap failed; <c>null</c> — no intent recorded, or the
    /// last one reached a terminal failure, caller should (re-)submit / fall through.
    /// </summary>
    private async Task<bool?> CheckRefundWithoutReceiverIntentAsync(ArkSwap swap, CancellationToken ct)
    {
        var existingIntentTxId = swap.Get(SwapMetadata.RefundIntentTxId);
        if (existingIntentTxId is null) return null;

        var intents = await _intentStorage.GetIntents(intentTxIds: [existingIntentTxId], cancellationToken: ct);
        var intent = intents.FirstOrDefault();
        if (intent is null) return null;

        // Re-arm the event trigger in case we restarted after saving the metadata.
        _intentToSwapId.TryAdd(existingIntentTxId, swap.SwapId);

        if (intent.State == ArkIntentState.BatchSucceeded)
        {
            await MarkSwapTerminalAsync(swap, ArkSwapStatus.Refunded, null, ct);
            _intentToSwapId.TryRemove(existingIntentTxId, out _);
            _logger?.LogInformation("Swap {SwapId}: refund-without-receiver batch succeeded", swap.SwapId);
            return true;
        }

        if (intent.State is ArkIntentState.WaitingToSubmit or ArkIntentState.WaitingForBatch or ArkIntentState.BatchInProgress)
        {
            _logger?.LogDebug("Swap {SwapId}: refund intent {IntentTxId} still in state {State} — waiting for batch",
                swap.SwapId, existingIntentTxId, intent.State);
            return false;
        }

        // Terminal failure (BatchFailed / Cancelled) — remove and signal re-submit.
        _logger?.LogWarning(
            "Swap {SwapId}: refund intent {IntentTxId} reached terminal failure state {State} — re-submitting",
            swap.SwapId, existingIntentTxId, intent.State);
        _intentToSwapId.TryRemove(existingIntentTxId, out _);
        return null;
    }

    /// <summary>Triggered when an in-flight refund intent's batch session completes (succeeds,
    /// fails, or is cancelled) — fires an immediate poll via the existing websocket trigger
    /// channel rather than waiting for the next routine poll tick.</summary>
    private void OnRefundIntentChanged(object? sender, ArkIntent intent)
    {
        if (!_intentToSwapId.TryGetValue(intent.IntentTxId, out var swapId))
            return;

        if (intent.State is ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled)
        {
            _logger?.LogInformation(
                "Refund intent {IntentTxId} for swap {SwapId} reached terminal state {State} — triggering poll",
                intent.IntentTxId, swapId, intent.State);
            _wsTriggerChannel.Writer.TryWrite(swapId);
        }
    }

    /// <summary>Persists a terminal status transition and unsubscribes the swap from the
    /// persistent websocket — the shared cleanup both the cooperative and batch-intent refund
    /// paths need once a swap reaches <see cref="ArkSwapStatus.Refunded"/>/<see cref="ArkSwapStatus.Failed"/>.</summary>
    private async Task MarkSwapTerminalAsync(ArkSwap swap, ArkSwapStatus status, string? failReason, CancellationToken ct)
    {
        var updated = swap with { Status = status, FailReason = failReason, UpdatedAt = DateTimeOffset.UtcNow };
        await _swapStorage.SaveSwap(swap.WalletId, updated, ct);
        SwapStatusChanged?.Invoke(this, new SwapStatusChangedEvent(swap.SwapId, swap.WalletId, Id, status, failReason));
        _swapsIdToWatch.TryRemove(swap.SwapId, out _);
        await UnsubscribeOnWebsocketAsync([swap.SwapId], ct);
    }

    // ─── WebSocket ─────────────────────────────────────────────────

    /// <summary>
    /// Single long-lived task owning the persistent Boltz websocket connection for the EVM
    /// swap leg. Mirrors <c>BoltzSwapProvider.RunWebsocketLoop</c> — one connection, repeated
    /// subscribe/unsubscribe ops keyed by swap id (per
    /// https://api.docs.boltz.exchange/api-v2.html#websocket) — reconnects with a 5s backoff
    /// and re-subscribes to the then-current <see cref="_swapsIdToWatch"/> snapshot.
    /// </summary>
    private async Task RunWebsocketLoop(CancellationToken ct)
    {
        var wsUri = _boltzClient.DeriveWebSocketUri();
        while (!ct.IsCancellationRequested)
        {
            BoltzWebsocketClient? client = null;
            try
            {
                _logger?.LogInformation("Connecting to Boltz websocket at {Uri} for EVM chain swaps", wsUri);
                client = new BoltzWebsocketClient(wsUri);
                client.OnAnyEventReceived += OnSwapEventReceived;
                await client.ConnectAsync(ct);

                string[] initialSubs;
                await _websocketLock.WaitAsync(ct);
                try
                {
                    _websocket = client;
                    initialSubs = _swapsIdToWatch.Keys.ToArray();
                }
                finally
                {
                    _websocketLock.Release();
                }

                if (initialSubs.Length > 0)
                {
                    await client.SubscribeAsync(initialSubs, ct);
                    _logger?.LogInformation(
                        "EVM swap websocket connected, subscribed to {Count} swap(s): [{SwapIds}]",
                        initialSubs.Length, string.Join(", ", initialSubs));
                }
                else
                {
                    _logger?.LogInformation("EVM swap websocket connected, no active swaps to subscribe yet");
                }

                await client.WaitUntilDisconnected(ct);
                _logger?.LogWarning("EVM swap websocket disconnected");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "EVM swap websocket error, reconnecting in 5s");
            }
            finally
            {
                await _websocketLock.WaitAsync(CancellationToken.None);
                try
                {
                    if (client is not null) client.OnAnyEventReceived -= OnSwapEventReceived;
                    if (ReferenceEquals(_websocket, client)) _websocket = null;
                }
                finally
                {
                    _websocketLock.Release();
                }
                if (client is not null) await client.DisposeAsync();
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(5000, ct);
        }
    }

    /// <summary>Subscribes additional swap ids on the current persistent websocket. No-ops when
    /// disconnected — the reconnect loop picks the ids up from <see cref="_swapsIdToWatch"/>.</summary>
    private async Task SubscribeOnWebsocketAsync(IReadOnlyList<string> swapIds, CancellationToken ct)
    {
        if (swapIds.Count == 0) return;
        await _websocketLock.WaitAsync(ct);
        try
        {
            if (_websocket is null)
            {
                _logger?.LogDebug(
                    "Skipping EVM websocket Subscribe: connection not yet up; reconnect loop will pick up [{SwapIds}]",
                    string.Join(", ", swapIds));
                return;
            }
            await _websocket.SubscribeAsync(swapIds.ToArray(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "EVM websocket Subscribe failed for [{SwapIds}]; reconnect loop will retry",
                string.Join(", ", swapIds));
        }
        finally
        {
            _websocketLock.Release();
        }
    }

    /// <summary>Unsubscribes swap ids from the current persistent websocket. Best-effort —
    /// leaving a terminal swap subscribed only costs a stray no-op push.</summary>
    private async Task UnsubscribeOnWebsocketAsync(IReadOnlyList<string> swapIds, CancellationToken ct)
    {
        if (swapIds.Count == 0) return;
        await _websocketLock.WaitAsync(ct);
        try
        {
            if (_websocket is null) return;
            await _websocket.UnsubscribeAsync(swapIds.ToArray(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "EVM websocket Unsubscribe failed for [{SwapIds}]; non-fatal",
                string.Join(", ", swapIds));
        }
        finally
        {
            _websocketLock.Release();
        }
    }

    private Task OnSwapEventReceived(WebSocketResponse? response)
    {
        try
        {
            if (response is { Event: "update", Channel: "swap.update", Args.Count: > 0 })
            {
                var swapUpdate = response.Args[0];
                var id = swapUpdate?["id"]?.GetValue<string>();
                if (id is not null)
                {
                    _logger?.LogDebug("EVM websocket event: swap {SwapId} status update", id);
                    _wsTriggerChannel.Writer.TryWrite(id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing EVM websocket event");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Decouples websocket event receipt from swap processing: <see cref="OnSwapEventReceived"/>
    /// only enqueues the swap id, this loop does the actual (potentially slow — REST call to
    /// Boltz, on-chain claim/refund) work, so a slow poll never delays draining the next
    /// websocket message.
    /// </summary>
    private async Task RunWsTriggerReaderAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var swapId in _wsTriggerChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var swaps = await _swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: ct);
                    var swap = swaps.FirstOrDefault(s => s.ProviderId == Id);
                    if (swap is null) continue;
                    await PollSwapAsync(swap, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogError(ex, "Websocket-triggered poll failed for swap {SwapId}", swapId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task<EvmChainClient> GetEvmChainClientAsync(CancellationToken ct)
    {
        if (_evmChainClient != null)
            return _evmChainClient;

        await _evmClientInitLock.WaitAsync(ct);
        try
        {
            if (_evmChainClient != null)
                return _evmChainClient;

            var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
            var account = new Account(_options.PrivateKey, info.Network.ChainId);
            var web3 = new Web3(account, _options.RpcUrl);
            _evmChainClient = new EvmChainClient(web3, info.SwapContracts.Erc20Swap);
            return _evmChainClient;
        }
        finally
        {
            _evmClientInitLock.Release();
        }
    }

    // ─── Local helpers (reimplemented rather than reaching into NArk.Swaps'
    // internal KeyExtensions/ParseSequence, which aren't visible from this
    // assembly — see plan's dependency-direction note) ─────────────────────

    private static OutputDescriptor ParseOutputDescriptor(string str, Network network)
    {
        if (!HexEncoder.IsWellFormed(str))
            return OutputDescriptor.Parse(str, network);

        var bytes = Convert.FromHexString(str);
        if (bytes.Length != 32 && bytes.Length != 33)
            throw new ArgumentException("the string must be 32/33 bytes long", nameof(str));

        return OutputDescriptor.Parse($"tr({str})", network);
    }

    private static Sequence ParseSequence(long val) =>
        val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);

    public void NotifyVtxoChanged(ArkVtxo vtxo) { }
    public void NotifySwapChanged(ArkSwap swap) { }

    public async ValueTask DisposeAsync()
    {
        _intentStorage.IntentChanged -= OnRefundIntentChanged;
        _shutdownCts.Cancel();
        _wsTriggerChannel.Writer.TryComplete();
        await Drain(_pollingTask);
        await Drain(_websocketTask);
        await Drain(_wsTriggerReaderTask);
        _shutdownCts.Dispose();
        _evmClientInitLock.Dispose();
        _websocketLock.Dispose();
    }
}
