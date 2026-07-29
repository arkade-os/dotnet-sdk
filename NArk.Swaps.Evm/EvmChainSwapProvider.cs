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
using NArk.Swaps.Evm.Dex;
using NArk.Swaps.Evm.Models;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
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
public partial class EvmChainSwapProvider : ISwapProvider
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

    /// <summary>
    /// Serialises EVM broadcasts sharing this provider's account. When a
    /// <see cref="DEXSwapService"/> is supplied, its <see cref="RouterClient"/> signs with the
    /// same key — so the caller must construct one <see cref="EvmNonceGuard"/>, hand it to that
    /// <see cref="RouterClient"/>, and pass it here too. Left to itself the provider makes its
    /// own, which is correct only for the plain tBTC flow where nothing else sends.
    /// </summary>
    private readonly EvmNonceGuard _nonceGuard;
    private readonly EvmSwapOptions _options;
    private readonly ILogger<EvmChainSwapProvider>? _logger;
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _pollingTask;
    private IEvmChainClient? _evmChainClient;
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
        DEXSwapService? dexSwapService = null,
        EvmNonceGuard? nonceGuard = null)
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
        _nonceGuard = nonceGuard ?? new EvmNonceGuard();
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

        // Derived from the descriptor that is ours on the Arkade leg — here the refund
        // descriptor, since this direction locks Arkade funds. Mirrors
        // SwapsManagementService.InitiateArkToBtcChainSwap's identical choice, and is what lets
        // a restore re-derive this preimage instead of losing it with local storage.
        preimage ??= await SwapPreimageDerivation.DeriveAsync(
            _walletProvider, walletId, refundDescriptor, index: 0, ct);
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

        // Before committing Arkade funds: the EVM leg we have to claim must expire safely before
        // this VHTLC's own refund opens. ClaimDetails is the EVM side in this direction.
        await ValidateTimeoutsAsync(
            response.Id, vhtlcContract.RefundLocktime,
            response.ClaimDetails?.TimeoutBlockHeight
                ?? throw new InvalidOperationException(
                    $"Chain swap {response.Id}: missing EVM claim details (timeoutBlockHeight)."),
            weClaimTheEvmLeg: true, ct);

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

        // Ours on the Arkade leg is the claim descriptor in this direction, mirroring
        // SwapsManagementService.InitiateBtcToArkChainSwap. This is the case
        // EvmChainSwapDiscoveryProvider's doc called out as unrecoverable: a random preimage
        // here left a restored ChainEvmToArk swap with its contract but no way to claim it.
        preimage ??= await SwapPreimageDerivation.DeriveAsync(
            _walletProvider, walletId, claimDescriptor, index: 0, ct);
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

        // Before committing EVM funds: the Arkade leg we have to claim must expire safely before
        // our own EVM lockup becomes refundable. LockupDetails is the EVM side in this direction.
        await ValidateTimeoutsAsync(
            response.Id, vhtlcContract.RefundLocktime,
            response.LockupDetails?.TimeoutBlockHeight
                ?? throw new InvalidOperationException(
                    $"Chain swap {response.Id}: missing EVM lockup details (timeoutBlockHeight)."),
            weClaimTheEvmLeg: false, ct);

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

    // ─── Timelock validation ────────────────────────────────────────────────

    /// <summary>
    /// Validates that the leg we claim expires safely before our own lockup becomes refundable,
    /// converting both legs' timeouts to wall clock first. Throws when
    /// <see cref="EvmSwapOptions.EnforceTimeoutValidation"/> is on, logs otherwise.
    /// </summary>
    /// <param name="swapId">Swap id, for the message.</param>
    /// <param name="arkRefundLocktime">The Arkade leg's refund locktime — height or timestamp.</param>
    /// <param name="evmTimeoutBlock">The EVM leg's absolute timeout block.</param>
    /// <param name="weClaimTheEvmLeg">
    /// True for <c>ChainArkToEvm</c> (we lock Arkade, claim EVM), false for <c>ChainEvmToArk</c>.
    /// Decides which of the two deadlines is the claim deadline and which is our refund.
    /// </param>
    private async Task ValidateTimeoutsAsync(
        string swapId, LockTime arkRefundLocktime, long evmTimeoutBlock, bool weClaimTheEvmLeg,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var chainTime = await _chainTimeProvider.GetChainTime(ct);
        var arkDeadline = arkRefundLocktime.IsTimeLock
            ? arkRefundLocktime.Date
            : EvmSwapTimeoutValidator.BlockToDeadline(
                now, chainTime.Height, arkRefundLocktime.Value, _options.ArkadeBlockTime);

        var client = await GetEvmChainClientAsync(ct);
        var evmBlock = await client.GetBlockNumberAsync(ct);
        var evmDeadline = EvmSwapTimeoutValidator.BlockToDeadline(
            now, (long)evmBlock, evmTimeoutBlock, _options.EvmBlockTime);

        var (claimDeadline, ourRefundAt) = weClaimTheEvmLeg
            ? (evmDeadline, arkDeadline)
            : (arkDeadline, evmDeadline);

        var violation = EvmSwapTimeoutValidator.Validate(
            now, claimDeadline, ourRefundAt, _options.MinClaimWindow, _options.MinTimeoutOrderingMargin);

        if (violation == SwapTimeoutViolation.None)
        {
            _logger?.LogDebug(
                "Swap {SwapId}: timeouts valid — claim by {ClaimDeadline:u}, our refund from {RefundAt:u}",
                swapId, claimDeadline, ourRefundAt);
            return;
        }

        var reason = EvmSwapTimeoutValidator.Describe(violation, now, claimDeadline, ourRefundAt);
        var message = $"Chain swap {swapId}: unsafe timelock arrangement — {reason}.";

        if (_options.EnforceTimeoutValidation)
            throw new InvalidOperationException(message);

        _logger?.LogWarning(
            "{Message} Continuing because EnforceTimeoutValidation is disabled — funds may not be recoverable.",
            message);
    }

    // ─── EVM transaction-hash bookkeeping ───────────────────────────────────
    // Every state-changing EVM call records its hash BEFORE waiting for the receipt, so a
    // lost receipt (RPC timeout, restart, dropped connection) stays distinguishable from
    // "never broadcast". Without this, a retry re-broadcasts a lock the contract will
    // reject as a duplicate preimage hash, and the caller marks the swap Failed while its
    // funds are already locked on-chain with nobody left watching them.

    /// <summary>
    /// Persists <paramref name="txHash"/> under <paramref name="metadataKey"/> on the swap
    /// record. Best-effort: a storage failure here must not abort an already-broadcast
    /// transaction, so it logs and continues — the on-chain event probes
    /// (<see cref="EvmChainClient.FindLockupEventAsync"/> and friends) are the durable
    /// backstop, this is the faster path that also covers the still-in-mempool window.
    /// </summary>
    private async Task RecordEvmTxIdAsync(string swapId, string metadataKey, string txHash, CancellationToken ct)
    {
        try
        {
            var swap = (await _swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: ct)).FirstOrDefault();
            if (swap is null)
            {
                _logger?.LogWarning(
                    "Swap {SwapId}: broadcast {Key}={TxHash} but the swap record is gone — cannot record it",
                    swapId, metadataKey, txHash);
                return;
            }

            await _swapStorage.SaveSwap(swap.WalletId, swap with
            {
                Metadata = new Dictionary<string, string>(swap.Metadata ?? []) { [metadataKey] = txHash },
                UpdatedAt = DateTimeOffset.UtcNow
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex,
                "Swap {SwapId}: failed to record {Key}={TxHash}; the on-chain event probe remains the backstop",
                swapId, metadataKey, txHash);
        }
    }

    /// <summary>Reads a previously recorded EVM transaction hash, or null if none.</summary>
    private async Task<string?> GetRecordedEvmTxIdAsync(string swapId, string metadataKey, CancellationToken ct)
    {
        var swap = (await _swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: ct)).FirstOrDefault();
        return swap?.Get(metadataKey);
    }

    private async Task<IEvmChainClient> GetEvmChainClientAsync(CancellationToken ct)
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
            _evmChainClient = new EvmChainClient(web3, info.SwapContracts.Erc20Swap, _nonceGuard);
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
}
