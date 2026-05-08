using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
using NArk.Core.Helpers;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NArk.Swaps.Utils;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using OutputDescriptorHelpers = NArk.Abstractions.Extensions.OutputDescriptorHelpers;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Boltz-specific swap provider implementing ISwapProvider.
/// Manages all Boltz protocol interactions: swap creation, status monitoring via
/// WebSocket/polling, cooperative claiming (MuSig2), and cooperative refunds.
/// </summary>
public class BoltzSwapProvider : ISwapProvider
{
    public const string Id = "boltz";

    private readonly BoltzSwapService _boltzService;
    private readonly ChainSwapMusigSession _chainSwapMusig;
    private readonly BoltzClient _boltzClient;
    private readonly BoltzLimitsValidator _limitsValidator;
    private readonly IClientTransport _clientTransport;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IWalletProvider _walletProvider;
    private readonly ISwapStorage _swapsStorage;
    private readonly IContractService _contractService;
    private readonly IContractStorage _contractStorage;
    private readonly ISafetyService _safetyService;
    private readonly SpendingService _spendingService;
    private readonly IChainTimeProvider _chainTimeProvider;
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;
    private readonly ILogger<BoltzSwapProvider>? _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<string> _triggerChannel = Channel.CreateUnbounded<string>();

    /// <summary>
    /// Set of swap ids currently being watched on the persistent Boltz
    /// websocket. Concurrent because <see cref="RunWebsocketLoop"/> reads
    /// it (under <see cref="_websocketLock"/>) on the websocket task while
    /// <see cref="DoUpdateStorage"/> and <see cref="PollSwapState"/> /
    /// <see cref="MarkSwapAsUnknownToProvider"/> mutate it on the channel
    /// reader thread. Modelled as a dictionary because there is no
    /// <c>ConcurrentHashSet&lt;T&gt;</c> in .NET; the byte payload is a
    /// placeholder and presence-of-key is the membership predicate.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _swapsIdToWatch = new();
    private readonly ConcurrentDictionary<string, string> _scriptToSwapId = [];

    /// <summary>
    /// Per-swap counter of consecutive <see cref="BoltzSwapNotFoundException"/>
    /// responses from <c>GetSwapStatusAsync</c>. Reset to zero on any successful
    /// status response. When a swap reaches
    /// <see cref="UnknownToProviderThreshold"/> consecutive 404s, the safety net
    /// in <see cref="MarkSwapAsUnknownToProvider"/> trips and the swap is
    /// transitioned to a terminal state. Concurrent because <c>NotifySwapChanged</c>
    /// (storage event thread) and <c>PollSwapState</c> (channel reader thread)
    /// can both touch the map.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _consecutiveUnknown = [];

    /// <summary>
    /// Number of consecutive Boltz 404s that must elapse before
    /// <c>PollSwapState</c> gives up on a swap and transitions it to a terminal
    /// state. At the 1-minute routine-poll cadence this is roughly a
    /// 10-minute grace window — long enough to ride out a transient Boltz
    /// route blip, short enough that a real "swap unknown to this provider"
    /// surfaces inside a working day.
    /// </summary>
    private const int UnknownToProviderThreshold = 10;

    private Task? _cacheTask;
    private Task? _routinePollTask;

    /// <summary>
    /// Long-lived task that owns the persistent Boltz websocket connection.
    /// One task for the lifetime of the provider — replaces the previous
    /// "cancel and recreate on every set change" pattern. Per the Boltz spec
    /// (https://api.docs.boltz.exchange/api-v2.html#websocket) the right
    /// model is one connection plus subscribe/unsubscribe for set changes.
    /// </summary>
    private Task? _websocketTask;
    /// <summary>The currently-connected client, or null when reconnecting / shutting down.</summary>
    private BoltzWebsocketClient? _websocket;
    /// <summary>
    /// Serialises subscribe/unsubscribe calls so two storage events firing
    /// at once can't interleave their websocket sends mid-payload.
    /// </summary>
    private readonly SemaphoreSlim _websocketLock = new(1, 1);
    private Network? _network;
    private ECXOnlyPubKey? _serverKey;

    public BoltzSwapProvider(
        BoltzClient boltzClient,
        BoltzLimitsValidator limitsValidator,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IWalletProvider walletProvider,
        ISwapStorage swapsStorage,
        IContractService contractService,
        IContractStorage contractStorage,
        ISafetyService safetyService,
        SpendingService spendingService,
        IIntentStorage intentStorage,
        IChainTimeProvider chainTimeProvider,
        ILogger<BoltzSwapProvider>? logger = null)
    {
        _boltzClient = boltzClient;
        _limitsValidator = limitsValidator;
        _clientTransport = clientTransport;
        _vtxoStorage = vtxoStorage;
        _walletProvider = walletProvider;
        _swapsStorage = swapsStorage;
        _contractService = contractService;
        _contractStorage = contractStorage;
        _safetyService = safetyService;
        _spendingService = spendingService;
        _chainTimeProvider = chainTimeProvider;
        _logger = logger;
        _boltzService = new BoltzSwapService(boltzClient, clientTransport);
        _chainSwapMusig = new ChainSwapMusigSession(boltzClient);
        _transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(
            clientTransport, safetyService, walletProvider, intentStorage);
    }

    public string ProviderId => Id;
    public string DisplayName => "Boltz";

    public bool SupportsRoute(SwapRoute route)
    {
        // Boltz supports:
        // Ark <-> Lightning (submarine / reverse submarine)
        // Ark <-> BTC on-chain (chain swaps)
        return route switch
        {
            { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.Lightning } => true,
            { Source.Network: SwapNetwork.Lightning, Destination.Network: SwapNetwork.Ark } => true,
            { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.BitcoinOnchain } => true,
            { Source.Network: SwapNetwork.BitcoinOnchain, Destination.Network: SwapNetwork.Ark } => true,
            _ => false
        };
    }

    public Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct)
    {
        IReadOnlyCollection<SwapRoute> routes = new[]
        {
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning),   // Submarine: Ark -> LN
            new SwapRoute(SwapAsset.BtcLightning, SwapAsset.ArkBtc),   // Reverse: LN -> Ark
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain),     // Chain: Ark -> BTC
            new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),     // Chain: BTC -> Ark
        };
        return Task.FromResult(routes);
    }

    public async Task StartAsync(string walletId, CancellationToken ct)
    {
        _logger?.LogInformation("Starting Boltz swap provider");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);

        var serverInfo = await _clientTransport.GetServerInfoAsync(ct);
        _serverKey = OutputDescriptorHelpers.Extract(serverInfo.SignerKey).XOnlyPubKey;
        _network = serverInfo.Network;

        // Seed the script→swap map from persistent storage so VTXOs arriving before
        // the first routine poll are still dispatched correctly. Without this, a
        // VTXO that hits a swap contract within the first minute after a restart
        // (or any pending swap carried over across restarts) silently no-ops in
        // NotifyVtxoChanged and the swap stalls until the user manually syncs.
        try
        {
            var existingActiveSwaps = await _swapsStorage.GetSwaps(active: true, cancellationToken: ct);
            foreach (var swap in existingActiveSwaps.Where(s => !string.IsNullOrEmpty(s.ContractScript)))
                _scriptToSwapId[swap.ContractScript] = swap.SwapId;
            _logger?.LogInformation("Seeded script→swap map with {Count} active swap(s)", _scriptToSwapId.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to seed script→swap map from storage; RoutinePoll will populate it on next tick");
        }

        _routinePollTask = RoutinePoll(TimeSpan.FromMinutes(1), multiToken.Token);
        _cacheTask = DoUpdateStorage(multiToken.Token);
        _websocketTask = RunWebsocketLoop(multiToken.Token);
    }

    public Task StopAsync(CancellationToken ct)
    {
        // Graceful shutdown: cancel the shutdown CTS and await the background
        // pumps. Without this, the IHostedService lifecycle calls StopAsync,
        // returns immediately, and the background tasks (websocket loop,
        // routine poll, channel reader) keep running until the host process
        // exits — leaking event handler subscriptions and emitting log lines
        // on a dying host. DisposeAsync is the safety net for non-hosted
        // scenarios; it short-circuits when the CTS is already cancelled.
        return ShutdownAsync();
    }

    private int _shutdownStarted;

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1) return;

        _logger?.LogInformation("Shutting down Boltz swap provider");
        try { await _shutdownCts.CancelAsync(); } catch { /* already cancelled */ }

        async Task Drain(Task? t)
        {
            if (t is null) return;
            try { await t; } catch { /* expected on cancel */ }
        }

        await Drain(_cacheTask);
        await Drain(_routinePollTask);
        await Drain(_websocketTask);
    }

    public async Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct)
    {
        var isReverse = route.Source.Network == SwapNetwork.Lightning;
        var isChain = route.Source.Network == SwapNetwork.BitcoinOnchain ||
                      route.Destination.Network == SwapNetwork.BitcoinOnchain;

        BoltzLimits? limits;
        if (isChain)
        {
            var isBtcToArk = route.Source.Network == SwapNetwork.BitcoinOnchain;
            limits = await _limitsValidator.GetChainLimitsAsync(isBtcToArk, ct);
        }
        else
        {
            limits = await _limitsValidator.GetLimitsAsync(isReverse, ct);
        }

        if (limits == null)
            throw new InvalidOperationException($"Unable to fetch Boltz limits for route {route}");

        return new SwapLimits
        {
            Route = route,
            MinAmount = limits.MinAmount,
            MaxAmount = limits.MaxAmount,
            FeePercentage = limits.FeePercentage,
            MinerFee = limits.MinerFee
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
            ExchangeRate = 1m // BTC-to-BTC, same asset
        };
    }

    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    // ─── Monitoring ────────────────────────────────────────────────

    /// <summary>
    /// Called by the router when a VTXO changes on a script associated with a Boltz swap.
    /// </summary>
    internal void NotifyVtxoChanged(ArkVtxo vtxo)
    {
        if (_network is null || _serverKey is null) return;

        try
        {
            if (_scriptToSwapId.TryGetValue(vtxo.Script, out var id))
            {
                _logger?.LogInformation(
                    "NotifyVtxoChanged: VTXO {Outpoint} on swap {SwapId}'s contract script (amount={Amount}, spent={Spent}) — triggering status poll",
                    vtxo.OutPoint, id, vtxo.Amount, vtxo.SpentByTransactionId is not null);
                _triggerChannel.Writer.TryWrite($"id:{id}");
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
    /// Called by the router when a swap record changes in storage.
    /// </summary>
    internal void NotifySwapChanged(ArkSwap swap)
    {
        // Keep the script→swap map up-to-date synchronously on every storage write.
        // Previously the map was only populated inside PollSwapState, so a VTXO
        // arriving on the swap contract between "swap saved" and "first poll
        // completes" would fire VtxosChanged, NotifyVtxoChanged would not find the
        // script in _scriptToSwapId, and the swap would stall until the next
        // routine poll (or a manual sync) populated the map.
        if (!string.IsNullOrEmpty(swap.ContractScript))
        {
            if (swap.Status is ArkSwapStatus.Refunded or ArkSwapStatus.Settled or ArkSwapStatus.Failed)
            {
                if (_scriptToSwapId.TryRemove(swap.ContractScript, out _))
                    _logger?.LogInformation(
                        "NotifySwapChanged: swap {SwapId} reached terminal {Status} — removed contract-script mapping",
                        swap.SwapId, swap.Status);
            }
            else
            {
                _scriptToSwapId[swap.ContractScript] = swap.SwapId;
                _logger?.LogInformation(
                    "NotifySwapChanged: swap {SwapId} storage event (type={Type}, status={Status}) — map now has {Count} entries",
                    swap.SwapId, swap.SwapType, swap.Status, _scriptToSwapId.Count);
            }
        }
        else
        {
            _logger?.LogDebug(
                "NotifySwapChanged: swap {SwapId} storage event (type={Type}, status={Status}) — no contract script yet",
                swap.SwapId, swap.SwapType, swap.Status);
        }

        _triggerChannel.Writer.TryWrite($"id:{swap.SwapId}");
    }

    private async Task RoutinePoll(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _triggerChannel.Writer.TryWrite("");
            await Task.Delay(interval, cancellationToken);
        }
    }

    private async Task DoUpdateStorage(CancellationToken cancellationToken)
    {
        await foreach (var eventDetails in _triggerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                if (eventDetails.StartsWith("id:"))
                {
                    var swapId = eventDetails[3..];

                    // If we already watch this swap, just poll — the
                    // persistent websocket already has it subscribed.
                    if (_swapsIdToWatch.ContainsKey(swapId))
                    {
                        _logger?.LogDebug("Swap {SwapId} update triggered (already monitored), polling state", swapId);
                        await PollSwapState([swapId], cancellationToken);
                    }
                    else
                    {
                        _logger?.LogInformation("New swap {SwapId} detected, subscribing on persistent websocket", swapId);
                        await PollSwapState([swapId], cancellationToken);

                        // Add to in-memory watch set + send a Subscribe op
                        // through the persistent websocket. No connection
                        // restart — that's the whole point of this fix.
                        _swapsIdToWatch.TryAdd(swapId, 0);
                        await SubscribeOnWebsocketAsync([swapId], cancellationToken);
                    }
                }
                else
                {
                    var activeSwaps =
                        await _swapsStorage.GetSwaps(active: true, cancellationToken: cancellationToken);
                    var newSwapIdSet =
                        activeSwaps.Select(s => s.SwapId).ToHashSet();

                    var currentIds = _swapsIdToWatch.Keys.ToHashSet();
                    if (currentIds.SetEquals(newSwapIdSet))
                    {
                        // Set unchanged, but still poll as a failsafe in case
                        // the websocket was disconnected between events.
                        if (newSwapIdSet.Count > 0)
                        {
                            _logger?.LogDebug("Routine poll: {Count} active swap(s), polling states as failsafe", newSwapIdSet.Count);
                            await PollSwapState(newSwapIdSet, cancellationToken);
                        }
                        continue;
                    }

                    var added = newSwapIdSet.Except(currentIds).ToArray();
                    var removed = currentIds.Except(newSwapIdSet).ToArray();
                    _logger?.LogInformation(
                        "Active swap set changed: {OldCount} -> {NewCount} swap(s); subscribing {Added}, unsubscribing {Removed}",
                        currentIds.Count, newSwapIdSet.Count, added.Length, removed.Length);

                    if (added.Length > 0)
                        await PollSwapState(added, cancellationToken);

                    foreach (var id in added) _swapsIdToWatch.TryAdd(id, 0);
                    foreach (var id in removed) _swapsIdToWatch.TryRemove(id, out _);

                    if (added.Length > 0)
                        await SubscribeOnWebsocketAsync(added, cancellationToken);
                    if (removed.Length > 0)
                        await UnsubscribeOnWebsocketAsync(removed, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Error processing swap update trigger: {Details}", eventDetails);
            }
        }
    }

    internal async Task PollSwapState(IEnumerable<string> idsToPoll, CancellationToken cancellationToken)
    {
        foreach (var idToPoll in idsToPoll)
        {
            try
            {
                _logger?.LogDebug("PollSwapState: querying Boltz for {SwapId}", idToPoll);
                var swapStatus = await _boltzClient.GetSwapStatusAsync(idToPoll, _shutdownCts.Token);
                // A successful response (any non-throwing path past GetSwapStatusAsync)
                // means Boltz still recognises this swap — clear the unknown counter.
                _consecutiveUnknown.TryRemove(idToPoll, out _);
                if (swapStatus?.Status is null)
                {
                    _logger?.LogDebug("Swap {SwapId}: Boltz returned null status", idToPoll);
                    continue;
                }
                _logger?.LogInformation("Swap {SwapId}: Boltz status '{BoltzStatus}'", idToPoll, swapStatus.Status);

                await using var @lock = await _safetyService.LockKeyAsync($"swap::{idToPoll}", cancellationToken);
                var swaps = await _swapsStorage.GetSwaps(swapIds: [idToPoll], cancellationToken: cancellationToken);
                var swap = swaps.FirstOrDefault();
                if (swap == null)
                {
                    _logger?.LogWarning("Swap {SwapId}: not found in storage", idToPoll);
                    continue;
                }

                // Tag every log line emitted during this iteration with the
                // owning wallet so per-wallet diagnostic-log capture can route
                // them — including transitive calls into TryClaimBtcForChainSwap,
                // TrySignBoltzBtcClaim, RequestRefundCooperatively. The using
                // block targets the foreach iteration body; its finally disposes
                // the scope before continue/break exits this iteration.
                using var _walletScope = _logger?.BeginScope(("WalletId", swap.WalletId));

                _scriptToSwapId[swap.ContractScript] = swap.SwapId;

                // Terminal states: nothing to do
                if (swap.Status is ArkSwapStatus.Refunded or ArkSwapStatus.Settled) continue;

                // Refresh VTXO state for the swap's contract script directly against arkd.
                // We cannot rely solely on the indexer subscription stream here: arkd does
                // not retroactively replay events, so if a VTXO lands between the moment
                // we subscribe and the moment we start reading the stream (or if a stream
                // reconnect drops a message), it never reaches NotifyVtxoChanged and the
                // swap stalls until a manual sync. Polling the single contract script per
                // status-check is cheap and closes that gap.
                if (!string.IsNullOrEmpty(swap.ContractScript))
                {
                    var freshCount = 0;
                    try
                    {
                        await foreach (var freshVtxo in _clientTransport.GetVtxoByScriptsAsSnapshot(
                                           new HashSet<string> { swap.ContractScript }, cancellationToken))
                        {
                            freshCount++;
                            await _vtxoStorage.UpsertVtxo(freshVtxo, cancellationToken);
                        }
                        _logger?.LogInformation(
                            "Swap {SwapId}: refreshed contract script — arkd returned {Count} VTXO(s)",
                            idToPoll, freshCount);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex, "Swap {SwapId}: failed to refresh VTXOs for contract script during status poll", idToPoll);
                    }
                }

                // Chain swap renegotiation: Boltz reports transaction.lockupFailed
                // when the funded amount doesn't match the quote it originally
                // returned. We ask Boltz for a new quote based on the actual
                // funded amount and accept it; if Boltz agrees the swap
                // continues with the renegotiated amount. If Boltz refuses
                // (amount outside limits etc.) we fall through to the refund
                // path below. Mirrors arkade-os/boltz-swap's `quoteSwap`.
                if (swap.SwapType is ArkSwapType.ChainBtcToArk or ArkSwapType.ChainArkToBtc &&
                    swap.Status is not (ArkSwapStatus.Settled or ArkSwapStatus.Refunded) &&
                    swapStatus.Status == "transaction.lockupFailed")
                {
                    if (await TryRenegotiateChainSwap(swap, cancellationToken))
                    {
                        // Boltz accepted the new quote — let the next poll
                        // observe the renegotiated swap making progress.
                        continue;
                    }
                    // Renegotiation refused — fall through to refund.
                }

                // If not refunded and status is refundable, start a coop refund
                if (swap.SwapType is ArkSwapType.Submarine && swap.Status is not ArkSwapStatus.Refunded &&
                    IsRefundableStatus(swapStatus.Status))
                {
                    _logger?.LogInformation("Swap {SwapId}: Boltz status '{BoltzStatus}' is refundable, initiating cooperative refund",
                        idToPoll, swapStatus.Status);
                    var newSwap =
                        swap with { Status = ArkSwapStatus.Failed, UpdatedAt = DateTimeOffset.Now };
                    await RequestRefundCooperatively(newSwap, cancellationToken);
                    // Don't map status to Failed below — if refund succeeded, status is already
                    // Refunded in storage; if it returned early (e.g. VTXOs not yet available
                    // due to batch round race), keep the swap Pending so routine polls retry.
                    continue;
                }

                // Chain swap cooperative refund: refundable status (after the
                // renegotiation attempt above already failed or this swap is
                // outright expired) means the user's locked funds need to come
                // back. BTC→ARK refunds the BTC lockup; ARK→BTC refunds the
                // Ark VHTLC. Both paths are best-effort — failure (Boltz
                // refuses, lockup not yet visible) keeps the swap Pending so
                // the routine poll retries, EXCEPT when Boltz reported
                // `swap.expired` and there's no lockup observable: at that
                // point the swap is dead and there's nothing to refund, so
                // we transition to Failed so callers can stop polling.
                if (swap.SwapType is ArkSwapType.ChainBtcToArk or ArkSwapType.ChainArkToBtc &&
                    swap.Status is not (ArkSwapStatus.Settled or ArkSwapStatus.Refunded) &&
                    IsRefundableStatus(swapStatus.Status))
                {
                    _logger?.LogInformation("Swap {SwapId}: Boltz status '{BoltzStatus}' is refundable for {SwapType}, attempting cooperative refund",
                        idToPoll, swapStatus.Status, swap.SwapType);
                    var refunded = swap.SwapType is ArkSwapType.ChainBtcToArk
                        ? await CoopRefundBtcToArkChainSwap(swap, cancellationToken)
                        : await CoopRefundArkToBtcChainSwap(swap, cancellationToken);
                    if (refunded) continue;

                    // Refund didn't succeed. If Boltz says `swap.expired`
                    // and there are no funds at the lockup, the swap is
                    // dead-with-nothing-to-recover — mark Failed so the
                    // routine poll stops retrying and the caller can move on.
                    var noBtcLockup = string.IsNullOrEmpty(swapStatus.Transaction?.Hex);
                    var noArkLockup = swap.SwapType == ArkSwapType.ChainArkToBtc
                        && (await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: cancellationToken)).Count == 0;
                    var nothingToRefund = swap.SwapType == ArkSwapType.ChainBtcToArk ? noBtcLockup : noArkLockup;
                    if (swapStatus.Status == "swap.expired" && nothingToRefund)
                    {
                        _logger?.LogInformation(
                            "Swap {SwapId}: expired with no observable lockup — marking Failed (no funds to recover)",
                            idToPoll);
                        await _swapsStorage.SaveSwap(swap.WalletId,
                            swap with
                            {
                                Status = ArkSwapStatus.Failed,
                                FailReason = "Swap expired before any funds were locked",
                                UpdatedAt = DateTimeOffset.UtcNow
                            }, cancellationToken);
                    }
                    // Otherwise leave Pending so the next routine poll retries
                    // the refund — the lockup might just not be visible yet.
                    continue;
                }

                // For ARK→BTC chain swaps: try to claim BTC when server has locked
                if (swap.SwapType is ArkSwapType.ChainArkToBtc &&
                    IsChainSwapClaimableStatus(swapStatus.Status))
                {
                    await TryClaimBtcForChainSwap(swap, cancellationToken);
                }

                // For BTC→ARK chain swaps: provide cooperative cross-signature so Boltz
                // can claim our BTC lockup via key-path (more efficient than script-path).
                // This is non-critical — Boltz can eventually claim via script-path with preimage.
                if (swap.SwapType is ArkSwapType.ChainBtcToArk &&
                    swapStatus.Status is "transaction.claim.pending")
                {
                    await TrySignBoltzBtcClaim(swap, cancellationToken);
                }

                // Re-read swap — claim handlers may have updated status to terminal
                var updatedSwaps = await _swapsStorage.GetSwaps(swapIds: [idToPoll], cancellationToken: cancellationToken);
                swap = updatedSwaps.FirstOrDefault() ?? swap;
                if (swap.Status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded) continue;

                var newStatus = MapBoltzStatus(swapStatus.Status);

                if (swap.Status == newStatus)
                {
                    _logger?.LogDebug(
                        "Swap {SwapId}: mapped Boltz '{BoltzStatus}' -> {Status}, unchanged",
                        idToPoll, swapStatus.Status, newStatus);
                    continue;
                }

                _logger?.LogInformation("Swap {SwapId}: status changed {OldStatus} -> {NewStatus} (Boltz: '{BoltzStatus}')",
                    idToPoll, swap.Status, newStatus, swapStatus.Status);

                var swapWithNewStatus =
                    swap with { Status = newStatus, UpdatedAt = DateTimeOffset.Now };

                await _swapsStorage.SaveSwap(swap.WalletId,
                    swapWithNewStatus, cancellationToken: cancellationToken);

                if (swapWithNewStatus.Status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded)
                {
                    _logger?.LogInformation("Swap {SwapId}: terminal state {Status}, removing from watch list",
                        idToPoll, swapWithNewStatus.Status);
                    _scriptToSwapId.Remove(swapWithNewStatus.ContractScript, out _);
                    _swapsIdToWatch.TryRemove(swapWithNewStatus.SwapId, out _);

                    // Drop the subscription on the persistent websocket so we
                    // don't keep receiving updates for a swap we no longer
                    // care about. Best-effort — failure is non-fatal.
                    await UnsubscribeOnWebsocketAsync([swapWithNewStatus.SwapId], cancellationToken);
                }
            }
            catch (BoltzSwapNotFoundException)
            {
                // Boltz has no record of this swap. This is the canonical failure
                // mode after a Boltz endpoint switch — old swap IDs are unknown
                // to the new instance. Track consecutive 404s; trip the safety
                // net only after the threshold to ride out transient blips.
                var count = _consecutiveUnknown.AddOrUpdate(idToPoll, 1, (_, c) => c + 1);
                _logger?.LogWarning(
                    "Swap {SwapId}: unknown to Boltz ({Count}/{Threshold} consecutive)",
                    idToPoll, count, UnknownToProviderThreshold);
                if (count >= UnknownToProviderThreshold)
                {
                    await MarkSwapAsUnknownToProvider(idToPoll, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Swap {SwapId}: error polling state from Boltz", idToPoll);
            }
        }
    }

    /// <summary>
    /// Transitions a swap to <see cref="ArkSwapStatus.Failed"/> once Boltz has
    /// consistently reported it unknown for <see cref="UnknownToProviderThreshold"/>
    /// consecutive polls. Called from <see cref="PollSwapState"/>'s
    /// <see cref="BoltzSwapNotFoundException"/> handler — at this point the
    /// swap is presumed permanently lost to the configured Boltz instance,
    /// typically because the operator pointed the SDK at a different Boltz
    /// endpoint than the one the swap was created on. After this transition
    /// the user must recover funds on-chain via the contract's script-path
    /// after CSV expiry; cooperative refund is no longer available because
    /// the original Boltz instance is unreachable.
    /// </summary>
    private async Task MarkSwapAsUnknownToProvider(string swapId, CancellationToken cancellationToken)
    {
        await using var @lock = await _safetyService.LockKeyAsync($"swap::{swapId}", cancellationToken);

        var swap = (await _swapsStorage.GetSwaps(swapIds: [swapId], cancellationToken: cancellationToken))
            .FirstOrDefault();
        if (swap is null)
        {
            _logger?.LogDebug("MarkSwapAsUnknownToProvider: swap {SwapId} no longer in storage", swapId);
            _consecutiveUnknown.TryRemove(swapId, out _);
            return;
        }

        // Idempotency: another caller (or a previous trip of this safety net)
        // may have already moved it terminal — nothing to do.
        if (!swap.Status.IsActive())
        {
            _consecutiveUnknown.TryRemove(swapId, out _);
            _swapsIdToWatch.TryRemove(swapId, out _);
            return;
        }

        var newMetadata = swap.Metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(swap.Metadata);
        newMetadata["unknownToProvider"] = "true";

        var newSwap = swap with
        {
            Status = ArkSwapStatus.Failed,
            FailReason = "Boltz no longer recognises this swap. " +
                         "Recover funds on-chain via the contract's script-path after CSV expiry.",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = newMetadata,
        };

        await _swapsStorage.SaveSwap(swap.WalletId, newSwap, cancellationToken: cancellationToken);

        _logger?.LogWarning(
            "Swap {SwapId}: marked Failed after {Threshold} consecutive Boltz 404s — swap is unknown to the configured Boltz instance",
            swapId, UnknownToProviderThreshold);

        // Stop polling and clear the counter. NotifySwapChanged handles
        // _scriptToSwapId eviction via the SaveSwap event we just emitted.
        _swapsIdToWatch.TryRemove(swapId, out _);
        _consecutiveUnknown.TryRemove(swapId, out _);
    }

    // ─── Cooperative Refund ────────────────────────────────────────

    /// <summary>
    /// Cooperative refund of an ARK→BTC chain swap whose Ark VHTLC lockup
    /// can't be redeemed (Boltz didn't lock BTC in time, swap expired,
    /// etc.). Builds an Ark refund tx spending the user's VHTLC back to a
    /// fresh receive address, asks Boltz to co-sign via
    /// <c>POST /v2/swap/chain/{id}/refund/ark</c>, submits via the existing
    /// Ark transaction builder, and marks the swap
    /// <see cref="ArkSwapStatus.Refunded"/>. Mirrors
    /// <see cref="RequestRefundCooperatively"/> for submarine swaps; the
    /// only differences are the Boltz API endpoint and the swap-type guard.
    /// </summary>
    private async Task<bool> CoopRefundArkToBtcChainSwap(ArkSwap swap, CancellationToken ct)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToBtc) return false;
        if (swap.Status == ArkSwapStatus.Refunded) return true;

        try
        {
            var serverInfo = await _clientTransport.GetServerInfoAsync(ct);

            var matchedSwapContracts =
                await _contractStorage.GetContracts(walletIds: [swap.WalletId], scripts: [swap.ContractScript],
                    cancellationToken: ct);
            var matchedSwapContractEntity = matchedSwapContracts.SingleOrDefault(e => e.Type == VHTLCContract.ContractType);
            if (matchedSwapContractEntity is null)
            {
                _logger?.LogWarning("Swap {SwapId}: VHTLC contract row not found for ARK→BTC refund", swap.SwapId);
                return false;
            }
            if (ArkContractParser.Parse(matchedSwapContractEntity.Type, matchedSwapContractEntity.AdditionalData,
                    serverInfo.Network) is not VHTLCContract contract)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to parse VHTLC contract for ARK→BTC refund", swap.SwapId);
                return false;
            }

            // Same arkd refresh pattern the submarine refund uses — close the
            // gap between the indexer subscription stream and what arkd
            // actually has on the contract script right now.
            await foreach (var freshVtxo in _clientTransport.GetVtxoByScriptsAsSnapshot(
                               new HashSet<string> { swap.ContractScript }, ct))
            {
                await _vtxoStorage.UpsertVtxo(freshVtxo, ct);
            }

            var vtxos = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: ct);
            if (vtxos.Count == 0)
            {
                _logger?.LogWarning("Swap {SwapId}: no VTXOs at VHTLC script for ARK→BTC refund", swap.SwapId);
                return false;
            }

            var vtxo = vtxos.Single();
            var timeHeight = await _chainTimeProvider.GetChainTime(ct);
            if (!vtxo.CanSpendOffchain(timeHeight))
            {
                _logger?.LogDebug("Swap {SwapId}: VHTLC VTXO not yet spendable offchain", swap.SwapId);
                return false;
            }

            var refundDest =
                await _contractService.DeriveContract(swap.WalletId, NextContractPurpose.SendToSelf,
                    ContractActivityState.AwaitingFundsBeforeDeactivate,
                    metadata: new Dictionary<string, string> { ["Source"] = $"chain-swap-refund:{swap.SwapId}" },
                    cancellationToken: ct);
            if (refundDest is null)
            {
                _logger?.LogError("Swap {SwapId}: failed to derive ARK→BTC refund destination", swap.SwapId);
                return false;
            }

            var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

            var (arkTx, checkpoints) = await _transactionBuilder.ConstructArkTransaction(
                [arkCoin],
                [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundDest.GetArkAddress())],
                serverInfo, ct);

            var checkpoint = checkpoints.Single();

            var refundResponse = await _boltzClient.RefundChainSwapArkAsync(swap.SwapId,
                new ChainArkRefundRequest
                {
                    Transaction = arkTx.ToBase64(),
                    Checkpoint = checkpoint.Psbt.ToBase64(),
                }, ct);

            var boltzSignedRefundPsbt = PSBT.Parse(refundResponse.Transaction, serverInfo.Network);
            var boltzSignedCheckpointPsbt = PSBT.Parse(refundResponse.Checkpoint, serverInfo.Network);
            arkTx.UpdateFrom(boltzSignedRefundPsbt);
            checkpoint.Psbt.UpdateFrom(boltzSignedCheckpointPsbt);

            await _transactionBuilder.SubmitArkTransaction([arkCoin], arkTx, [checkpoint], ct);

            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.UtcNow },
                ct);
            _logger?.LogInformation("Swap {SwapId}: ARK→BTC cooperative refund completed", swap.SwapId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: ARK→BTC cooperative refund failed", swap.SwapId);
            return false;
        }
    }

    /// <summary>
    /// Cooperative refund of a BTC→ARK chain swap whose user-funded BTC
    /// lockup couldn't be redeemed (renegotiation refused, swap expired,
    /// etc.). Asks Boltz for a MuSig2 partial signature on a refund tx that
    /// spends the lockup back to the user's stored BTC destination, signs
    /// our half, broadcasts via Boltz's BTC broadcaster, and marks the
    /// swap <see cref="ArkSwapStatus.Refunded"/>. Returns <c>false</c> on
    /// any failure (Boltz refuses, lockup tx not yet observable, etc.) so
    /// the routine poll loop will retry on the next tick.
    /// </summary>
    /// <remarks>
    /// The signing primitive (<see cref="ChainSwapMusigSession.CooperativeRefundAsync"/>)
    /// already existed; this method wires the lookup of the lockup tx,
    /// outpoint discovery, refund-tx construction, and broadcast around it.
    /// Mirrors the symmetry of <see cref="TryClaimBtcForChainSwap"/>.
    /// </remarks>
    private async Task<bool> CoopRefundBtcToArkChainSwap(ArkSwap swap, CancellationToken ct)
    {
        if (swap.SwapType != ArkSwapType.ChainBtcToArk) return false;
        if (swap.Status == ArkSwapStatus.Refunded) return true;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);
        var btcAddress = swap.Get(SwapMetadata.BtcAddress);

        if (string.IsNullOrEmpty(ephemeralKeyHex) ||
            string.IsNullOrEmpty(boltzResponseJson) ||
            string.IsNullOrEmpty(btcAddress))
        {
            _logger?.LogWarning("Swap {SwapId}: missing chain-swap metadata for BTC refund", swap.SwapId);
            return false;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            // For BTC→ARK refund the lockup is on BTC — held by `lockupDetails`,
            // not `claimDetails` (claimDetails is for the Ark side which Boltz
            // is going to reverse). Refund spends the user's BTC lockup back
            // to the user-supplied refund destination.
            var lockupDetails = response?.LockupDetails;
            if (lockupDetails?.SwapTree is null || string.IsNullOrEmpty(lockupDetails.ServerPublicKey))
            {
                _logger?.LogWarning("Swap {SwapId}: BTC lockup details missing from Boltz response, can't refund", swap.SwapId);
                return false;
            }

            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(lockupDetails.ServerPublicKey));

            var serverInfo = await _clientTransport.GetServerInfoAsync(ct);
            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                lockupDetails.SwapTree, userPubKey, boltzPubKey,
                lockupDetails.LockupAddress, serverInfo.Network);
            var refundDest = BitcoinAddress.Create(btcAddress, serverInfo.Network);

            var swapStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, ct);
            if (string.IsNullOrEmpty(swapStatus?.Transaction?.Hex))
            {
                _logger?.LogDebug("Swap {SwapId}: BTC lockup tx not yet observable from Boltz, deferring refund", swap.SwapId);
                return false;
            }

            var lockupTx = Transaction.Parse(swapStatus.Transaction.Hex, serverInfo.Network);
            var lockupScript = BitcoinAddress.Create(lockupDetails.LockupAddress, serverInfo.Network).ScriptPubKey;
            var vout = -1;
            for (var i = 0; i < lockupTx.Outputs.Count; i++)
            {
                if (lockupTx.Outputs[i].ScriptPubKey == lockupScript) { vout = i; break; }
            }
            if (vout < 0)
            {
                _logger?.LogWarning("Swap {SwapId}: lockup tx has no output paying to {Address}", swap.SwapId, lockupDetails.LockupAddress);
                return false;
            }

            var outpoint = new OutPoint(lockupTx.GetHash(), vout);
            var prevOut = lockupTx.Outputs[vout];

            // Same fee shape as TryClaimBtcForChainSwap. 250 sat at low-fee
            // regimes is fine; high-fee mempools warrant a real estimator —
            // the existing claim path has the same trade-off, hoist
            // separately when needed.
            const long feeSats = 250L;
            var unsignedRefundTx = BtcTransactionBuilder.BuildKeyPathClaimTx(outpoint, prevOut, refundDest, feeSats);

            _logger?.LogInformation("Swap {SwapId}: requesting MuSig2 cooperative BTC refund", swap.SwapId);
            var signedTx = await _chainSwapMusig.CooperativeRefundAsync(
                swap.SwapId, unsignedRefundTx, prevOut, inputIndex: 0,
                ecPrivKey, boltzPubKey, spendInfo, ct);

            var broadcastResult = await _boltzClient.BroadcastBtcTransactionAsync(
                new BroadcastRequest { Hex = signedTx.ToHex() }, ct);
            _logger?.LogInformation("Swap {SwapId}: BTC refund broadcast — txid={TxId}", swap.SwapId, broadcastResult.Id);

            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.UtcNow },
                ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: BTC cooperative refund failed", swap.SwapId);
            return false;
        }
    }

    /// <summary>
    /// Asks Boltz for a new chain-swap quote based on the amount actually
    /// funded at the lockup, and accepts it. Returns <c>true</c> on success
    /// (quote returned and accepted, local <see cref="ArkSwap.ExpectedAmount"/>
    /// updated). Returns <c>false</c> if Boltz refuses the quote — typically
    /// because the funded amount falls outside Boltz's published limits, in
    /// which case the caller should fall through to the refund path.
    /// </summary>
    /// <remarks>
    /// Wired into <see cref="PollSwapState"/> on the
    /// <c>transaction.lockupFailed</c> Boltz status, mirroring the
    /// <c>quoteSwap</c> behaviour in <c>arkade-os/boltz-swap</c>'s TS SDK.
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

            await _boltzClient.AcceptChainQuoteAsync(swap.SwapId, newQuote, ct);
            _logger?.LogInformation(
                "Swap {SwapId}: chain quote renegotiated — original {Original} sats → new {New} sats",
                swap.SwapId, swap.ExpectedAmount, newQuote.Amount);

            var updated = swap with
            {
                ExpectedAmount = newQuote.Amount,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _swapsStorage.SaveSwap(swap.WalletId, updated, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Boltz returns 4xx for funded amounts outside its limits —
            // the caller treats this as "renegotiation refused, refund instead".
            _logger?.LogWarning(ex,
                "Swap {SwapId}: chain quote renegotiation refused by Boltz", swap.SwapId);
            return false;
        }
    }

    internal async Task RequestRefundCooperatively(ArkSwap swap, CancellationToken cancellationToken = default)
    {
        if (swap.SwapType != ArkSwapType.Submarine)
        {
            throw new InvalidOperationException("Only submarine swaps can be refunded");
        }

        if (swap.Status == ArkSwapStatus.Refunded)
        {
            return;
        }

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var matchedSwapContracts =
            await _contractStorage.GetContracts(walletIds: [swap.WalletId], scripts: [swap.ContractScript],
                cancellationToken: cancellationToken);

        var matchedSwapContractForSwapWallet =
            matchedSwapContracts.Single(entity => entity.Type == VHTLCContract.ContractType);

        // Parse the VHTLC contract
        if (ArkContractParser.Parse(matchedSwapContractForSwapWallet.Type,
                matchedSwapContractForSwapWallet.AdditionalData, serverInfo.Network) is not VHTLCContract contract)
        {
            throw new InvalidOperationException("Failed to parse VHTLC contract for refund");
        }

        // Poll arkd directly for VTXOs at the swap script.
        await foreach (var freshVtxo in _clientTransport.GetVtxoByScriptsAsSnapshot(
                           new HashSet<string> { swap.ContractScript }, cancellationToken))
        {
            await _vtxoStorage.UpsertVtxo(freshVtxo, cancellationToken);
        }

        // Get VTXOs for this contract
        var vtxos = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript],
            cancellationToken: cancellationToken);
        if (vtxos.Count == 0)
        {
            _logger?.LogWarning("Swap {SwapId}: no VTXOs found for cooperative refund", swap.SwapId);
            return;
        }

        // Use the first VTXO (should only be one for a swap)
        var vtxo = vtxos.Single();

        var timeHeight = await _chainTimeProvider.GetChainTime(cancellationToken);
        if (!vtxo.CanSpendOffchain(timeHeight))
            return;

        // Get the user's wallet address for refund destination
        var refundAddress =
            await _contractService.DeriveContract(swap.WalletId, NextContractPurpose.SendToSelf,
                ContractActivityState.AwaitingFundsBeforeDeactivate,
                metadata: new Dictionary<string, string> { ["Source"] = $"swap:{swap.SwapId}" },
                cancellationToken: cancellationToken);
        if (refundAddress == null)
        {
            throw new InvalidOperationException("Failed to get refund address");
        }

        try
        {
            var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

            var (arkTx, checkpoints) =
                await _transactionBuilder.ConstructArkTransaction([arkCoin],
                    [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundAddress.GetArkAddress())],
                    serverInfo, cancellationToken);

            var checkpoint = checkpoints.Single();

            // Request Boltz to co-sign the refund
            var refundRequest = new SubmarineRefundRequest
            {
                Transaction = arkTx.ToBase64(),
                Checkpoint = checkpoint.Psbt.ToBase64()
            };

            var refundResponse =
                await _boltzClient.RefundSubmarineSwapAsync(swap.SwapId, refundRequest, cancellationToken);

            // Parse Boltz-signed transactions
            var boltzSignedRefundPsbt = PSBT.Parse(refundResponse.Transaction, serverInfo.Network);
            var boltzSignedCheckpointPsbt = PSBT.Parse(refundResponse.Checkpoint, serverInfo.Network);

            // Combine signatures
            arkTx.UpdateFrom(boltzSignedRefundPsbt);
            checkpoint.Psbt.UpdateFrom(boltzSignedCheckpointPsbt);

            await _transactionBuilder.SubmitArkTransaction([arkCoin], arkTx, [checkpoint],
                cancellationToken);

            var newSwap =
                swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.Now };

            await _swapsStorage.SaveSwap(newSwap.WalletId, newSwap, cancellationToken);
            _logger?.LogInformation("Swap {SwapId}: cooperative refund completed successfully", swap.SwapId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Swap {SwapId}: cooperative refund failed, deactivating refund contract", swap.SwapId);
            await _contractStorage.SaveContract(
                refundAddress.ToEntity(swap.WalletId, activityState: ContractActivityState.Inactive),
                cancellationToken);
            throw;
        }

        // Synchronization barrier
        try
        {
            await using var @lock =
                await _safetyService.LockKeyAsync($"contract::{contract.GetArkAddress().ScriptPubKey.ToHex()}",
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Refund already succeeded — cancellation during disposal is benign.
        }
    }

    // ─── Status Mapping ────────────────────────────────────────────

    internal static ArkSwapStatus MapBoltzStatus(string status)
    {
        return status switch
        {
            "swap.created" or "invoice.set" => ArkSwapStatus.Pending,
            "invoice.failedToPay" or "invoice.expired" or "swap.expired" or "transaction.failed"
                or "transaction.refunded" =>
                ArkSwapStatus.Failed,
            "transaction.mempool" or "transaction.confirmed" => ArkSwapStatus.Pending,
            "invoice.settled" or "transaction.claimed" => ArkSwapStatus.Settled,
            // Chain swap specific statuses
            "transaction.server.mempool" or "transaction.server.confirmed"
                or "transaction.claim.pending" => ArkSwapStatus.Pending,
            "transaction.lockupFailed" => ArkSwapStatus.Failed,
            _ => ArkSwapStatus.Unknown
        };
    }

    internal static bool IsRefundableStatus(string status)
    {
        return status switch
        {
            "invoice.failedToPay" => true,
            "invoice.expired" => true,
            "swap.expired" => true,
            "transaction.lockupFailed" => true,
            _ => false
        };
    }

    private static bool IsChainSwapClaimableStatus(string status)
    {
        return status is "transaction.server.mempool" or "transaction.server.confirmed";
    }

    // ─── WebSocket ─────────────────────────────────────────────────

    /// <summary>
    /// Single long-lived task that owns the Boltz websocket connection.
    /// Connects, subscribes to the current <see cref="_swapsIdToWatch"/>
    /// snapshot, and listens until the connection drops; on drop it
    /// reconnects with a 5-second backoff and re-subscribes to the
    /// then-current watch set. Subscribe / unsubscribe ops for runtime set
    /// changes ride this same connection via <see cref="SubscribeOnWebsocketAsync"/>
    /// and <see cref="UnsubscribeOnWebsocketAsync"/> — there is no longer
    /// a per-set-change connection restart.
    /// </summary>
    /// <remarks>
    /// Mirrors the model documented at
    /// https://api.docs.boltz.exchange/api-v2.html#websocket — one
    /// connection, repeated subscribe/unsubscribe ops keyed by swap id.
    /// </remarks>
    private async Task RunWebsocketLoop(CancellationToken cancellationToken)
    {
        var wsUri = _boltzClient.DeriveWebSocketUri();
        while (!cancellationToken.IsCancellationRequested)
        {
            BoltzWebsocketClient? client = null;
            try
            {
                _logger?.LogInformation("Connecting to Boltz websocket at {Uri}", wsUri);
                client = new BoltzWebsocketClient(wsUri);
                client.OnAnyEventReceived += OnSwapEventReceived;
                await client.ConnectAsync(cancellationToken);

                // Publish under the lock so subscribe/unsubscribe callers
                // see a consistent snapshot. Snapshot the watch set first so
                // the initial Subscribe doesn't race a concurrent mutation.
                string[] initialSubs;
                await _websocketLock.WaitAsync(cancellationToken);
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
                    await client.SubscribeAsync(initialSubs, cancellationToken);
                    _logger?.LogInformation(
                        "Boltz websocket connected, subscribed to {Count} swap(s): [{SwapIds}]",
                        initialSubs.Length, string.Join(", ", initialSubs));
                }
                else
                {
                    _logger?.LogInformation("Boltz websocket connected, no active swaps to subscribe yet");
                }

                await client.WaitUntilDisconnected(cancellationToken);
                _logger?.LogWarning("Boltz websocket disconnected");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Boltz websocket error, reconnecting in 5s");
            }
            finally
            {
                // Clear the published reference under the same lock; pending
                // sub/unsub callers will see _websocket==null and short-circuit
                // (the next reconnect re-subscribes from _swapsIdToWatch).
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

            if (!cancellationToken.IsCancellationRequested)
                await Task.Delay(5000, cancellationToken);
        }
    }

    /// <summary>
    /// Subscribe additional swap ids on the current persistent websocket.
    /// No-ops when the websocket is disconnected — the reconnect loop will
    /// pick the ids up from <see cref="_swapsIdToWatch"/> on its next attempt.
    /// </summary>
    private async Task SubscribeOnWebsocketAsync(IReadOnlyList<string> swapIds, CancellationToken cancellationToken)
    {
        if (swapIds.Count == 0) return;
        await _websocketLock.WaitAsync(cancellationToken);
        try
        {
            if (_websocket is null)
            {
                _logger?.LogDebug("Skipping websocket Subscribe: connection not yet up; reconnect loop will pick up [{SwapIds}]",
                    string.Join(", ", swapIds));
                return;
            }
            await _websocket.SubscribeAsync(swapIds.ToArray(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "websocket Subscribe failed for [{SwapIds}]; reconnect loop will retry",
                string.Join(", ", swapIds));
        }
        finally
        {
            _websocketLock.Release();
        }
    }

    /// <summary>
    /// Unsubscribe swap ids from the current persistent websocket. Failures
    /// are logged and swallowed — leaving a terminal swap subscribed costs
    /// only a stray status push that the channel reader will route to a
    /// no-op poll.
    /// </summary>
    private async Task UnsubscribeOnWebsocketAsync(IReadOnlyList<string> swapIds, CancellationToken cancellationToken)
    {
        if (swapIds.Count == 0) return;
        await _websocketLock.WaitAsync(cancellationToken);
        try
        {
            if (_websocket is null) return;
            await _websocket.UnsubscribeAsync(swapIds.ToArray(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "websocket Unsubscribe failed for [{SwapIds}]; swap is already terminal locally so this is non-fatal",
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
            if (response is null)
                return Task.CompletedTask;

            if (response.Event == "update" && response is { Channel: "swap.update", Args.Count: > 0 })
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]!.GetValue<string>();
                    var status = swapUpdate["status"]?.GetValue<string>();
                    _logger?.LogDebug("Websocket event: swap {SwapId} status '{Status}'", id, status);
                    _triggerChannel.Writer.TryWrite($"id:{id}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing websocket event");
        }

        return Task.CompletedTask;
    }

    // ─── Claiming (ARK→BTC) ───────────────────────────────────────

    internal async Task TryClaimBtcForChainSwap(ArkSwap swap, CancellationToken cancellationToken)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToBtc)
            return;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);
        var preimageHex = swap.Get(SwapMetadata.Preimage);
        var btcAddress = swap.Get(SwapMetadata.BtcAddress);

        if (string.IsNullOrEmpty(ephemeralKeyHex) ||
            string.IsNullOrEmpty(boltzResponseJson) ||
            string.IsNullOrEmpty(preimageHex) ||
            string.IsNullOrEmpty(btcAddress))
        {
            _logger?.LogWarning("Swap {SwapId}: missing chain swap metadata for BTC claim", swap.SwapId);
            return;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            if (response == null)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to deserialize Boltz response", swap.SwapId);
                return;
            }

            var claimDetails = response.ClaimDetails;
            if (claimDetails?.SwapTree == null || claimDetails.ServerPublicKey == null)
            {
                _logger?.LogWarning("Swap {SwapId}: no BTC claim details (swapTree or serverPublicKey is null)", swap.SwapId);
                return;
            }

            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(claimDetails.ServerPublicKey));

            var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                claimDetails.SwapTree, userPubKey, boltzPubKey,
                claimDetails.LockupAddress, serverInfo.Network);
            var btcDest = BitcoinAddress.Create(btcAddress, serverInfo.Network);

            // Get the lockup transaction from Boltz's status response
            var swapStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, cancellationToken);
            if (swapStatus?.Transaction?.Hex == null)
            {
                _logger?.LogDebug("Swap {SwapId}: lockup tx hex not yet available", swap.SwapId);
                return;
            }

            // Parse the lockup tx and find the output matching the HTLC address
            var lockupTx = Transaction.Parse(swapStatus.Transaction.Hex, serverInfo.Network);
            var lockupScript = BitcoinAddress.Create(claimDetails.LockupAddress, serverInfo.Network).ScriptPubKey;
            var vout = -1;
            for (var i = 0; i < lockupTx.Outputs.Count; i++)
            {
                if (lockupTx.Outputs[i].ScriptPubKey == lockupScript)
                {
                    vout = i;
                    break;
                }
            }

            if (vout < 0)
            {
                _logger?.LogWarning("Swap {SwapId}: no output matching HTLC address {Address}", swap.SwapId, claimDetails.LockupAddress);
                return;
            }

            var outpoint = new OutPoint(lockupTx.GetHash(), vout);
            var prevOut = lockupTx.Outputs[vout];

            // Build unsigned claim tx
            var feeSats = 250L;
            var unsignedClaimTx = BtcTransactionBuilder.BuildKeyPathClaimTx(outpoint, prevOut, btcDest, feeSats);

            Transaction signedTx;
            try
            {
                _logger?.LogInformation("Swap {SwapId}: attempting MuSig2 cooperative BTC claim", swap.SwapId);
                signedTx = await _chainSwapMusig.CooperativeClaimAsync(
                    swap.SwapId, preimageHex, unsignedClaimTx, prevOut, 0,
                    ecPrivKey, boltzPubKey, spendInfo, cancellationToken);
            }
            catch (Exception coopEx)
            {
                _logger?.LogWarning(coopEx, "Swap {SwapId}: MuSig2 cooperative claim failed, falling back to script-path", swap.SwapId);

                // Fallback: script-path claim with preimage
                var claimLeaf = BtcHtlcScripts.GetClaimLeaf(claimDetails.SwapTree);
                var preimageBytes = Convert.FromHexString(preimageHex);
                BtcTransactionBuilder.SignScriptPathClaim(
                    unsignedClaimTx, 0, prevOut, spendInfo, claimLeaf,
                    preimageBytes, ephemeralKey);
                signedTx = unsignedClaimTx;
            }

            // Broadcast the signed claim transaction
            var broadcastResult = await _boltzClient.BroadcastBtcTransactionAsync(
                new BroadcastRequest { Hex = signedTx.ToHex() }, cancellationToken);

            _logger?.LogInformation("Swap {SwapId}: BTC claimed! txid={TxId}", swap.SwapId, broadcastResult.Id);

            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Status = ArkSwapStatus.Settled, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Swap {SwapId}: error claiming BTC", swap.SwapId);
        }
    }

    // ─── Cross-Signing (BTC→ARK) ──────────────────────────────────

    internal async Task TrySignBoltzBtcClaim(ArkSwap swap, CancellationToken cancellationToken)
    {
        if (swap.SwapType != ArkSwapType.ChainBtcToArk)
            return;

        // Only cross-sign once — avoid sending duplicate signatures on repeated polls
        if (swap.Get(SwapMetadata.CrossSigned) == "true")
            return;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);

        if (string.IsNullOrEmpty(ephemeralKeyHex) || string.IsNullOrEmpty(boltzResponseJson))
        {
            _logger?.LogWarning("Swap {SwapId}: missing chain swap metadata for cooperative BTC claim signing", swap.SwapId);
            return;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            if (response == null)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to deserialize Boltz response for cross-signing", swap.SwapId);
                return;
            }

            var lockupDetails = response.LockupDetails;
            if (lockupDetails?.SwapTree == null || lockupDetails.ServerPublicKey == null)
            {
                _logger?.LogWarning("Swap {SwapId}: no BTC lockup details (swapTree or serverPublicKey is null)", swap.SwapId);
                return;
            }

            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(lockupDetails.ServerPublicKey));

            var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                lockupDetails.SwapTree, userPubKey, boltzPubKey,
                lockupDetails.LockupAddress, serverInfo.Network);

            _logger?.LogInformation("Swap {SwapId}: providing cooperative MuSig2 cross-signature for Boltz BTC claim", swap.SwapId);
            await _chainSwapMusig.CrossSignBoltzClaimAsync(
                swap.SwapId, ecPrivKey, boltzPubKey, spendInfo, cancellationToken);

            _logger?.LogInformation("Swap {SwapId}: cooperative cross-signature sent successfully", swap.SwapId);

            // Mark as cross-signed to avoid sending duplicate signatures
            var metadata = new Dictionary<string, string>(swap.Metadata ?? [])
            {
                [SwapMetadata.CrossSigned] = "true"
            };
            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Metadata = metadata, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-critical: Boltz can still claim via script-path with the preimage
            _logger?.LogWarning(ex, "Swap {SwapId}: cooperative cross-signing failed (non-critical, Boltz will use script-path)", swap.SwapId);
        }
    }

    // ─── Swap Creation (delegated from SwapsManagementService) ────

    internal BoltzSwapService BoltzService => _boltzService;

    // ─── Swap Restoration ──────────────────────────────────────────

    internal async Task<RestorableSwap[]> RestoreSwapsFromBoltzAsync(
        string[] publicKeys, CancellationToken ct)
    {
        return (await _boltzClient.RestoreSwapsAsync(publicKeys, ct))
            .Where(swap => swap.From == "ARK" || swap.To == "ARK").ToArray();
    }

    internal async Task<SubmarineRefundResponse> RefundSubmarineSwapAsync(
        string swapId, SubmarineRefundRequest request, CancellationToken ct)
    {
        return await _boltzClient.RefundSubmarineSwapAsync(swapId, request, ct);
    }

    // ─── Disposal ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        // ShutdownAsync handles the cancel + drain. It's idempotent — safe
        // to call after StopAsync has already run during host shutdown.
        await ShutdownAsync();
        _websocketLock.Dispose();
        _shutdownCts.Dispose();
    }
}
