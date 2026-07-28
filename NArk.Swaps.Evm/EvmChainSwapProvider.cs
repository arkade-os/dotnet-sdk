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

    /// <summary>
    /// Milestone 4's USDT/generic-ERC20 DEX-hop support — null unless the caller explicitly
    /// constructs one (no DI wiring/production <see cref="IDexQuoteProvider"/> exists yet, see
    /// that interface's TODO). <see cref="LockEvmFromErc20Async"/>/<see cref="ClaimEvmLockupToErc20Async"/>
    /// throw if this is null; the plain tBTC flow (<see cref="LockEvmAsync"/>,
    /// <see cref="TryClaimEvmLockupAsync"/>) never needs it.
    /// </summary>
    private readonly DEXSwapService? _dexSwapService;
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

    /// <summary>Maps a swap's Ark contract script → swap id, mirroring
    /// <c>BoltzSwapProvider</c>'s own map — lets <see cref="NotifyVtxoChanged"/> react the
    /// instant a VTXO lands on a tracked script instead of waiting for the next poll tick.
    /// Kept fresh by <see cref="NotifySwapChanged"/> and seeded from storage in
    /// <see cref="StartAsync"/> so swaps carried over across a restart aren't blind until the
    /// first routine poll runs.</summary>
    private readonly ConcurrentDictionary<string, string> _scriptToSwapId = new();
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
        ILogger<EvmChainSwapProvider>? logger = null,
        DEXSwapService? dexSwapService = null)
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
        _dexSwapService = dexSwapService;
        _options = options.Value;
        _logger = logger;
        _transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(
            clientTransport, safetyService, walletProvider, intentStorage);
    }

    public string ProviderId => Id;
    public string DisplayName => "Boltz (EVM)";

    /// <summary>
    /// This provider's own EVM account address, derived from <see cref="EvmSwapOptions.PrivateKey"/>
    /// — the same key <see cref="GetEvmChainClientAsync"/> signs lock/claim/refund transactions
    /// with. For <c>ChainArkToEvm</c>, this MUST be the <c>evmClaimAddress</c> passed to
    /// <see cref="CreateArkToEvmSwapAsync"/> (Boltz locks tBTC for whoever this address is, and
    /// only this provider's own key can later claim it). Address derivation from a private key
    /// doesn't touch the network, so this is synchronous — chain id only affects transaction
    /// signing (EIP-155), not address derivation.
    /// </summary>
    public string EvmAddress => new Account(_options.PrivateKey).Address;

    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    // ─── Routes ─────────────────────────────────────────────────────────────

    // TODO: hardcoded to the single ArkBtc<->ArbitrumTbtc pair (matching EvmSwapOptions.PairCurrency).
    // Milestone 4 (USDT/generic ERC20 via the Router's DEX-hop) needs both of these to become
    // route/asset-driven instead of a fixed pair — e.g. a SwapAsset.ArbitrumUsdt route entry once
    // that leg exists.
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

    // TODO: ExchangeRate is hardcoded to 1m — correct only because both legs of this pair are
    // BTC-pegged (ARK BTC <-> Arbitrum tBTC). Boltz's /v2/swap/chain pair response already
    // carries a real `rate` field (see EvmChainPairDetails.Rate, currently unused/dropped by
    // GetLimitsAsync), which will need to be threaded through here once Milestone 4 adds a
    // non-1:1 asset (USDT) to this provider's routes.
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

    /// <summary>
    /// Locks <paramref name="result"/>'s tBTC amount in <c>ERC20Swap</c> for a
    /// <c>ChainEvmToArk</c> swap created via <see cref="CreateEvmToArkSwapAsync"/> — approve +
    /// lock in one call, using the claim address/timelock/amount Boltz returned in
    /// <c>result.Swap.LockupDetails</c>. Unlike the ARK/BTC legs (where the swap-creation
    /// response describes an address the counterparty pays into), the EVM leg's lock
    /// parameters are ones <em>we</em> choose when calling the contract — Boltz's response just
    /// tells us its own claim address so Boltz can later claim what we lock. Not idempotent:
    /// the contract reverts on a second lock with the same preimage hash, so call this once per
    /// swap.
    /// </summary>
    // TODO: no caller-side idempotency guard (e.g. checking FindLockupEventAsync first) — if
    // InitiateEvmToArkChainSwap's caller retries after a transient failure post-broadcast (tx
    // sent but the receipt wait/response was lost), this will revert on the second attempt
    // instead of detecting the existing lockup and treating it as success.
    public async Task LockEvmAsync(EvmChainSwapResult result, CancellationToken ct = default)
    {
        if (result.Swap.LockupDetails is not { ClaimAddress: { } claimAddress } lockupDetails)
            throw new InvalidOperationException(
                $"Chain swap {result.Swap.Id}: missing EVM lockup details (claimAddress) — not a ChainEvmToArk swap?");

        var client = await GetEvmChainClientAsync(ct);
        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];

        await client.ApproveTokenAsync(tokenAddress, lockupDetails.Amount, ct);
        await client.LockAsync(result.PreimageHash, lockupDetails.Amount, tokenAddress, claimAddress,
            lockupDetails.TimeoutBlockHeight, ct);

        _logger?.LogInformation("Swap {SwapId}: locked {Amount} tBTC in ERC20Swap for Boltz to claim",
            result.Swap.Id, lockupDetails.Amount);
    }

    /// <summary>
    /// Milestone 4 alternative to <see cref="LockEvmAsync"/>: funds the same
    /// <c>ChainEvmToArk</c> lockup from an arbitrary ERC20 (e.g. USDT) instead of tBTC directly,
    /// via <see cref="DEXSwapService.LockViaDexHopAsync"/> — one atomic transaction that pulls
    /// <paramref name="tokenInAddress"/> via Permit2, swaps it to tBTC, and locks the result.
    /// This provider's own <see cref="EvmAddress"/> both signs the Permit2 witness and is used
    /// as the refund address.
    /// </summary>
    // TODO: not reachable from SwapsManagementServiceEvmExtensions/the normal swap-creation flow
    // yet, and there's no caller-side idempotency guard (see LockEvmAsync's identical TODO) —
    // this is the atomic-mechanics half of Milestone 4; a real IDexQuoteProvider (Uniswap V3) is
    // the other half, still unimplemented (see that interface's TODO).
    public async Task LockEvmFromErc20Async(
        EvmChainSwapResult result, string tokenInAddress, BigInteger amountIn, CancellationToken ct = default)
    {
        if (_dexSwapService is null)
            throw new InvalidOperationException(
                "No DEXSwapService configured for this provider — pass one to EvmChainSwapProvider's constructor.");
        if (result.Swap.LockupDetails is not { ClaimAddress: { } claimAddress } lockupDetails)
            throw new InvalidOperationException(
                $"Chain swap {result.Swap.Id}: missing EVM lockup details (claimAddress) — not a ChainEvmToArk swap?");

        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];
        var ownerKey = new EthECKey(_options.PrivateKey);

        await _dexSwapService.LockViaDexHopAsync(
            ownerKey, tokenInAddress, amountIn, tokenAddress, result.PreimageHash, claimAddress, EvmAddress,
            lockupDetails.TimeoutBlockHeight,
            permit2Nonce: new BigInteger(RandomUtils.GetBytes(8), isUnsigned: true),
            permit2Deadline: DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            ct: ct);

        _logger?.LogInformation(
            "Swap {SwapId}: swapped {AmountIn} of {TokenIn} to tBTC via Router and locked it for Boltz to claim",
            result.Swap.Id, amountIn, tokenInAddress);
    }

    /// <summary>
    /// Milestone 4 alternative to the automatic claim path (<see cref="TryClaimEvmLockupAsync"/>,
    /// which the poll loop always uses): claims this swap's tBTC lockup and atomically swaps the
    /// proceeds to <paramref name="outputTokenAddress"/> via
    /// <see cref="DEXSwapService.ClaimAndSwapAsync"/>, instead of keeping tBTC. Returns the
    /// amount swept in the output token.
    /// </summary>
    // TODO: caller-invoked only — the poll loop has no way to know a caller wants this instead of
    // the plain claim (would need a new SwapMetadata field recording the desired output token,
    // consulted by PollSwapAsync/TryClaimEvmLockupAsync — not designed yet). A real
    // IDexQuoteProvider is also still unimplemented, same as LockEvmFromErc20Async.
    public async Task<BigInteger> ClaimEvmLockupToErc20Async(
        ArkSwap swap, string outputTokenAddress, CancellationToken ct = default)
    {
        if (_dexSwapService is null)
            throw new InvalidOperationException(
                "No DEXSwapService configured for this provider — pass one to EvmChainSwapProvider's constructor.");

        var preimageHex = swap.Get(SwapMetadata.Preimage)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: missing preimage in metadata.");
        var preimage = Convert.FromHexString(preimageHex);

        var client = await GetEvmChainClientAsync(ct);
        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];
        var lockup = await client.FindLockupEventAsync(Hashes.SHA256(preimage), ct)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: no Lockup event found yet.");

        var claimKey = new EthECKey(_options.PrivateKey);
        var swept = await _dexSwapService.ClaimAndSwapAsync(
            claimKey, preimage, lockup.Amount, tokenAddress, lockup.RefundAddress, lockup.Timelock,
            outputTokenAddress, ct);

        await MarkSwapTerminalAsync(swap, ArkSwapStatus.Settled, null, ct);
        _logger?.LogInformation("Swap {SwapId}: claimed EVM lockup and swapped {Swept} to {OutputToken}",
            swap.SwapId, swept, outputTokenAddress);
        return swept;
    }

    // ─── Lifecycle: websocket push (primary) + REST poll loop (safety net) ─────────

    public async Task StartAsync(CancellationToken ct)
    {
        // Seed the script→swap map from storage so a VTXO arriving before the first routine
        // poll (e.g. right after a restart, for a swap that was already active) still dispatches
        // correctly — mirrors BoltzSwapProvider.Lifecycle.cs's StartAsync exactly.
        try
        {
            var existingActiveSwaps = await _swapStorage.GetSwaps(
                swapTypes: [ArkSwapType.ChainArkToEvm, ArkSwapType.ChainEvmToArk], active: true, cancellationToken: ct);
            foreach (var swap in existingActiveSwaps.Where(s => s.ProviderId == Id && !string.IsNullOrEmpty(s.ContractScript)))
                _scriptToSwapId[swap.ContractScript] = swap.SwapId;
            _logger?.LogInformation("Seeded script→swap map with {Count} active swap(s)", _scriptToSwapId.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to seed script→swap map from storage; RunPollLoopAsync will pick up on next tick");
        }

        _pollingTask = RunPollLoopAsync(_shutdownCts.Token);
        _websocketTask = RunWebsocketLoop(_shutdownCts.Token);
        _wsTriggerReaderTask = RunWsTriggerReaderAsync(_shutdownCts.Token);
        _intentStorage.IntentChanged += OnRefundIntentChanged;
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
            case EvmSwapAction.CanRenegotiateChain:
            {
                if (await TryRenegotiateChainSwap(swap, ct))
                {
                    // Renegotiation accepted — re-poll immediately (against the freshly
                    // persisted ExpectedAmount) so the claim fires this cycle rather than
                    // waiting for the next tick, mirroring BoltzSwapProvider's equivalent path.
                    var refreshed = (await _swapStorage.GetSwaps(swapIds: [swap.SwapId], cancellationToken: ct))
                        .FirstOrDefault() ?? swap;
                    await PollSwapAsync(refreshed, ct);
                    return;
                }

                // Boltz refused the quote (funded amount outside its limits) — fall back to
                // refunding whichever side we locked, mirroring BoltzSwapProvider's fallback.
                if (swap.SwapType == ArkSwapType.ChainArkToEvm)
                    await TryCoopRefundArkToEvm(swap, ct);
                else
                    await TryRefundEvmLockupAsync(swap, ct);
                return;
            }
            case EvmSwapAction.CanClaimEvmLockup:
                await TryClaimEvmLockupAsync(swap, ct);
                return;
            case EvmSwapAction.CanRefundEvmLockup:
                await TryRefundEvmLockupAsync(swap, ct);
                return;
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

    /// <summary>
    /// Asks Boltz for a new chain-swap quote based on the amount actually funded at the
    /// lockup, and accepts it. Returns <c>true</c> on success (quote returned and accepted,
    /// local <see cref="ArkSwap.ExpectedAmount"/> updated). Returns <c>false</c> if Boltz
    /// refuses the quote — typically because the funded amount falls outside Boltz's published
    /// limits for this pair — in which case the caller should fall through to the refund path.
    /// </summary>
    /// <remarks>
    /// Wired into <see cref="PollSwapAsync"/> on the <c>transaction.lockupFailed</c> Boltz
    /// status. Mirrors <c>NArk.Swaps.Boltz.BoltzSwapProvider.TryRenegotiateChainSwap</c>
    /// exactly — same currency-agnostic <c>GET</c>/<c>POST v2/swap/chain/{id}/quote</c>
    /// endpoints via the shared <see cref="BoltzClient"/>, just bounded against this pair's own
    /// limits (<see cref="GetLimitsAsync"/>) instead of <c>BoltzLimitsValidator</c>, which
    /// hardcodes the <c>BTC</c>/<c>ARK</c> pair keys and can't see our <c>TBTC</c>/etc. pair.
    /// </remarks>
    private async Task<bool> TryRenegotiateChainSwap(ArkSwap swap, CancellationToken ct)
    {
        try
        {
            var newQuote = await _boltzClient.GetChainQuoteAsync(swap.SwapId, ct);
            if (newQuote is null)
            {
                _logger?.LogWarning("Swap {SwapId}: Boltz returned a null chain quote", swap.SwapId);
                return false;
            }

            // Bound the renegotiated amount before accepting it and persisting it as the
            // swap's new ExpectedAmount, same rationale as the Boltz-native path: a 0/negative
            // quote is a parse/protocol bug, and an out-of-limits amount would be rejected by
            // AcceptChainQuoteAsync anyway, but checking locally avoids a wire round-trip.
            if (swap.Route is null)
            {
                _logger?.LogWarning("Swap {SwapId}: no Route recorded, cannot validate renegotiated quote", swap.SwapId);
                return false;
            }
            var limits = await GetLimitsAsync(swap.Route, ct);
            if (newQuote.Amount <= 0 || newQuote.Amount < limits.MinAmount || newQuote.Amount > limits.MaxAmount)
            {
                _logger?.LogWarning(
                    "Swap {SwapId}: rejecting renegotiated chain quote with out-of-bounds amount {Amount} sats " +
                    "(limits: min={Min}, max={Max})",
                    swap.SwapId, newQuote.Amount, limits.MinAmount, limits.MaxAmount);
                return false;
            }

            await _boltzClient.AcceptChainQuoteAsync(swap.SwapId, newQuote, ct);
            _logger?.LogInformation(
                "Swap {SwapId}: chain quote renegotiated — original {Original} sats -> new {New} sats",
                swap.SwapId, swap.ExpectedAmount, newQuote.Amount);

            var updated = swap with
            {
                ExpectedAmount = newQuote.Amount,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _swapStorage.SaveSwap(swap.WalletId, updated, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Boltz returns 4xx both for out-of-limits amounts and for an already-accepted
            // quote (e.g. an overlapping poll tick won the race). Disambiguate by re-reading
            // server-side status: if Boltz has moved the swap past lockupFailed, renegotiation
            // effectively succeeded.
            try
            {
                var currentStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, ct);
                if (currentStatus is not null &&
                    !string.IsNullOrEmpty(currentStatus.Status) &&
                    !string.Equals(currentStatus.Status, BoltzSwapStatus.TransactionLockupFailed, StringComparison.Ordinal))
                {
                    _logger?.LogInformation(
                        "Swap {SwapId}: AcceptChainQuoteAsync 4xx'd but Boltz status is {Status} — " +
                        "treating as renegotiated by a concurrent poll",
                        swap.SwapId, currentStatus.Status);
                    return true;
                }
            }
            catch (Exception probeEx) when (probeEx is not OperationCanceledException)
            {
                _logger?.LogDebug(probeEx,
                    "Swap {SwapId}: status probe after renegotiation failure also failed; falling back to refund",
                    swap.SwapId);
            }

            _logger?.LogWarning(ex, "Swap {SwapId}: chain quote renegotiation refused by Boltz", swap.SwapId);
            return false;
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
        // Boltz is the one who locked this side of the swap. A null here (classifier already
        // saw Boltz report the lockup as mempool/confirmed) means our own indexer view just
        // hasn't caught up yet — transient, so throwing and retrying next tick is correct,
        // unlike the permanent "never locked" case in TryRefundEvmLockupAsync below.
        var lockup = await client.FindLockupEventAsync(Hashes.SHA256(preimage), ct)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: no Lockup event found yet.");

        await client.ClaimAsync(preimage, lockup.Amount, tokenAddress, lockup.RefundAddress, lockup.Timelock, ct);

        // Set status ourselves rather than waiting for Boltz's own indexer to notice we spent
        // its lockup and flip transaction.claimed — Boltz has strong incentive to track this
        // promptly (it's their funds moving) but nothing here should depend on an external
        // party's monitoring being fast, or even present.
        await MarkSwapTerminalAsync(swap, ArkSwapStatus.Settled, null, ct);
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

        var lockup = await client.FindLockupEventAsync(preimageHash, ct);
        if (lockup is null)
        {
            // Swap expired before we ever locked (LockEvmAsync never ran, or the caller never
            // funded it) — nothing to refund. Unlike TryClaimEvmLockupAsync's null case, this
            // is permanent, not transient: mark Failed so the poll loop stops retrying forever.
            if (swap.Status != ArkSwapStatus.Failed)
            {
                _logger?.LogInformation(
                    "Swap {SwapId}: expired with no EVM lockup observed — marking Failed", swap.SwapId);
                await MarkSwapTerminalAsync(swap, ArkSwapStatus.Failed, "Swap expired before any funds were locked", ct);
            }
            return;
        }

        await client.RefundAsync(preimageHash, lockup.Amount, tokenAddress, lockup.ClaimAddress, lockup.Timelock, ct);

        // Same rationale as TryClaimEvmLockupAsync — but more important here: this is OUR OWN
        // refund of OUR OWN funds, and empirically (verified live this session) Boltz's own
        // status can stay stuck on swap.expired indefinitely since it has no direct incentive
        // to track a refund that doesn't move its funds. Waiting on Boltz's indexer here would
        // leave the swap Pending forever despite the refund having already succeeded on-chain.
        await MarkSwapTerminalAsync(swap, ArkSwapStatus.Refunded, null, ct);
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

    /// <summary>
    /// Called by <c>SwapsManagementService</c> when a VTXO changes on ANY tracked script across
    /// ALL registered providers, not just ours — mirrors <c>BoltzSwapProvider.NotifyVtxoChanged</c>.
    /// Scripts belonging to other providers simply won't be in <see cref="_scriptToSwapId"/>, so
    /// this naturally no-ops for them.
    /// </summary>
    public void NotifyVtxoChanged(ArkVtxo vtxo)
    {
        try
        {
            if (_scriptToSwapId.TryGetValue(vtxo.Script, out var id))
            {
                _logger?.LogInformation(
                    "NotifyVtxoChanged: VTXO {Outpoint} on swap {SwapId}'s contract script (amount={Amount}, spent={Spent}) — triggering status poll",
                    vtxo.OutPoint, id, vtxo.Amount, vtxo.SpentByTransactionId is not null);
                _wsTriggerChannel.Writer.TryWrite(id);
            }
            else
            {
                _logger?.LogDebug(
                    "NotifyVtxoChanged: VTXO {Outpoint} on script {Script} — no swap mapping, ignoring",
                    vtxo.OutPoint, vtxo.Script);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NotifyVtxoChanged: error dispatching VTXO {Outpoint}", vtxo.OutPoint);
        }
    }

    /// <summary>
    /// Called by <c>SwapsManagementService</c> when ANY swap record changes in storage, not just
    /// ours — mirrors <c>BoltzSwapProvider.NotifySwapChanged</c>. The unconditional trigger write
    /// at the end for a foreign swap id is harmless: <see cref="RunWsTriggerReaderAsync"/> already
    /// filters by <c>s.ProviderId == Id</c>.
    /// </summary>
    public void NotifySwapChanged(ArkSwap swap)
    {
        if (!string.IsNullOrEmpty(swap.ContractScript))
        {
            if (swap.Status.IsTerminalState())
            {
                if (_scriptToSwapId.TryRemove(swap.ContractScript, out _))
                    _logger?.LogInformation(
                        "NotifySwapChanged: swap {SwapId} reached terminal {Status} — removed contract-script mapping",
                        swap.SwapId, swap.Status);
            }
            else
            {
                _scriptToSwapId[swap.ContractScript] = swap.SwapId;
                _logger?.LogDebug(
                    "NotifySwapChanged: swap {SwapId} storage event (type={Type}, status={Status}) — map now has {Count} entries",
                    swap.SwapId, swap.SwapType, swap.Status, _scriptToSwapId.Count);
            }
        }

        _wsTriggerChannel.Writer.TryWrite(swap.SwapId);
    }

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
