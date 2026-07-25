using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
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
/// Non-cooperative path only for this milestone — see the plan's deferred-scope section.
/// </summary>
public class EvmChainSwapProvider : ISwapProvider
{
    public const string Id = "boltz-evm";

    private readonly BoltzClient _boltzClient;
    private readonly IClientTransport _clientTransport;
    private readonly ISwapStorage _swapStorage;
    private readonly IContractService _contractService;
    private readonly EvmSwapOptions _options;
    private readonly ILogger<EvmChainSwapProvider>? _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _pollingTask;
    private EvmChainClient? _evmChainClient;
    private readonly SemaphoreSlim _evmClientInitLock = new(1, 1);

    public EvmChainSwapProvider(
        BoltzClient boltzClient,
        IClientTransport clientTransport,
        ISwapStorage swapStorage,
        IContractService contractService,
        IOptions<EvmSwapOptions> options,
        ILogger<EvmChainSwapProvider>? logger = null)
    {
        _boltzClient = boltzClient;
        _clientTransport = clientTransport;
        _swapStorage = swapStorage;
        _contractService = contractService;
        _options = options.Value;
        _logger = logger;
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

    // ─── Lifecycle: simple poll loop (no websocket yet — see plan) ─────────

    public Task StartAsync(CancellationToken ct)
    {
        _pollingTask = RunPollLoopAsync(_shutdownCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _shutdownCts.Cancel();
        if (_pollingTask != null)
            await _pollingTask;
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

                foreach (var swap in swaps.Where(s => s.ProviderId == Id))
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
                _logger?.LogWarning(
                    "Swap {SwapId}: action {Action} is not implemented yet (Ark-side spending — see plan follow-up scope)",
                    swap.SwapId, action);
                break;
        }

        var terminal = BoltzSwapStatus.ToArkSwapStatus(status.Status);
        if (terminal != null && terminal != swap.Status)
        {
            await _swapStorage.UpdateSwapStatus(swap.WalletId, swap.SwapId, terminal.Value, status.FailureReason, ct);
            SwapStatusChanged?.Invoke(this,
                new SwapStatusChangedEvent(swap.SwapId, swap.WalletId, Id, terminal.Value, status.FailureReason));
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
        _shutdownCts.Cancel();
        if (_pollingTask != null)
            await _pollingTask;
        _shutdownCts.Dispose();
        _evmClientInitLock.Dispose();
    }
}
