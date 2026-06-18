using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Helpers;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Boltz-specific swap provider implementing ISwapProvider.
/// Manages all Boltz protocol interactions: swap creation, status monitoring via
/// WebSocket/polling, cooperative claiming (MuSig2), and cooperative refunds.
/// </summary>
public partial class BoltzSwapProvider : ISwapProvider
{
    public const string Id = "boltz";

    private readonly BoltzSwapService _boltzService;
    private readonly ChainSwapMusigSession _chainSwapMusig;
    private readonly BoltzClient _boltzClient;
    private readonly BoltzLimitsValidator _limitsValidator;
    private readonly IClientTransport _clientTransport;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly ISwapStorage _swapsStorage;
    private readonly IContractService _contractService;
    private readonly IContractStorage _contractStorage;
    private readonly ISafetyService _safetyService;
    private readonly IBitcoinBlockchain _chainTimeProvider;
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;
    private readonly IIntentStorage _intentStorage;
    private readonly IIntentGenerationService? _intentGenerationService;
    private readonly ILogger<BoltzSwapProvider>? _logger;

    /// <summary>Maps refund intent txId → swapId so <see cref="OnRefundIntentChanged"/> can
    /// trigger a poll when the batch session for a refund-without-receiver intent completes.</summary>
    private readonly ConcurrentDictionary<string, string> _intentToSwapId = new();

    private readonly CancellationTokenSource _shutdownCts = new();
    /// <summary>
    /// Linked CTS produced inside StartAsync that joins the caller's token and our
    /// internal shutdown token. Stored as a field so it gets disposed on shutdown
    /// instead of leaking the registration handle for the provider's lifetime.
    /// </summary>
    private CancellationTokenSource? _linkedStartCts;

    /// <summary>
    /// Flat fee in sats used for cooperative refund / claim BTC transactions.
    /// 250 sats is ~1 sat/vB for a single-input single-output taproot key-path
    /// spend, which is fine in low-fee regimes but becomes pessimistic when
    /// the mempool is congested. TODO: plumb <c>IFeeEstimator</c> here so the
    /// fee scales with the mempool rather than getting stuck.
    /// </summary>
    private const long DefaultRefundClaimFeeSats = 250L;
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
        IIntentStorage intentStorage,
        IBitcoinBlockchain chainTimeProvider,
        IIntentGenerationService? intentGenerationService = null,
        ILogger<BoltzSwapProvider>? logger = null)
    {
        _boltzClient = boltzClient;
        _limitsValidator = limitsValidator;
        _clientTransport = clientTransport;
        _vtxoStorage = vtxoStorage;
        _swapsStorage = swapsStorage;
        _contractService = contractService;
        _contractStorage = contractStorage;
        _safetyService = safetyService;
        _chainTimeProvider = chainTimeProvider;
        _intentStorage = intentStorage;
        _intentGenerationService = intentGenerationService;
        _logger = logger;
        _boltzService = new BoltzSwapService(boltzClient, clientTransport);
        _chainSwapMusig = new ChainSwapMusigSession(boltzClient);
        _transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(
            clientTransport, safetyService, walletProvider, intentStorage);
    }

    public string ProviderId => Id;
    public string DisplayName => "Boltz";

    public bool SupportsRoute(SwapRoute route) => BoltzRouteHelper.SupportsRoute(route);

    public Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct) =>
        BoltzRouteHelper.GetAvailableRoutesAsync(ct);

    public Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct) =>
        BoltzRouteHelper.GetLimitsAsync(route, _limitsValidator, ct);

    public Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct) =>
        BoltzRouteHelper.GetQuoteAsync(route, amount, _limitsValidator, ct);

    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    /// <summary>
    /// Raises <see cref="SwapStatusChanged"/> for <paramref name="swap"/>'s new status.
    /// Subscriber exceptions are swallowed (logged) so a misbehaving consumer can't
    /// take down the poll loop. Call this after persisting the new status to
    /// storage so subscribers see a state consistent with the DB.
    /// </summary>
    private void RaiseSwapStatusChanged(ArkSwap swap, string? failReason = null)
    {
        var handler = SwapStatusChanged;
        if (handler is null) return;

        try
        {
            handler.Invoke(this, new SwapStatusChangedEvent(
                SwapId: swap.SwapId,
                WalletId: swap.WalletId,
                ProviderId: ProviderId,
                NewStatus: swap.Status,
                FailReason: failReason));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Swap {SwapId}: SwapStatusChanged handler threw — recovery loop continues",
                swap.SwapId);
        }
    }

    // ─── Monitoring ────────────────────────────────────────────────

    /// <summary>
    /// Called by the router when a VTXO changes on a script associated with a Boltz swap.
    /// </summary>
    public void NotifyVtxoChanged(ArkVtxo vtxo)
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
    public void NotifySwapChanged(ArkSwap swap)
    {
        // Keep the script→swap map up-to-date synchronously on every storage write.
        // Previously the map was only populated inside PollSwapState, so a VTXO
        // arriving on the swap contract between "swap saved" and "first poll
        // completes" would fire VtxosChanged, NotifyVtxoChanged would not find the
        // script in _scriptToSwapId, and the swap would stall until the next
        // routine poll (or a manual sync) populated the map.
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

    /// <summary>
    /// Fire-and-forget delayed retry: re-enqueue a swap id on the trigger
    /// channel after a short delay. Used by <see cref="RequestSubmarineCoopRefund"/>
    /// when an early-return is caused by a transient race (canonical VTXO not
    /// yet visible at the swap script). Without this the next opportunity to
    /// re-attempt is the 1-minute routine poll, which can stack to multiple
    /// cycles on slow CI runners and blow through the test budget. Uses the
    /// provider's shutdown token so the retry survives the current
    /// <c>PollSwapState</c> call's cancellation but stops on provider shutdown.
    /// </summary>
    private void ScheduleNearTermRetry(string swapId, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _shutdownCts.Token);
                _triggerChannel.Writer.TryWrite($"id:{swapId}");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Near-term retry scheduling for swap {SwapId} aborted", swapId);
            }
        });
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
                using var walletScope = _logger?.BeginScope(("WalletId", swap.WalletId));

                _scriptToSwapId[swap.ContractScript] = swap.SwapId;

                // Terminal states: nothing to do
                if (swap.Status.IsTerminalState()) continue;

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

                switch (BoltzOperationClassifier.Classify(swap, swapStatus.Status))
                {
                    case BoltzSwapAction.CanCoopRefundSubmarine:
                        await RequestSubmarineCoopRefund(swap, swapStatus, cancellationToken);
                        continue;
                    
                    case BoltzSwapAction.CanCoopRefundArkToBtc:
                        await TryCoopRefundArkToBtc(swap, swapStatus, cancellationToken);
                        continue;
                    
                    case BoltzSwapAction.CanCoopRefundBtcToArk:
                        await TryRefundBtcToArk(swap, swapStatus, cancellationToken);
                        continue;

                    case BoltzSwapAction.CanRenegotiateChain:
                    {
                        if (await TryRenegotiateChainSwap(swap, cancellationToken))
                        {
                            // Renegotiation accepted — re-poll immediately so the claim
                            // fires in this cycle rather than waiting for the next tick.
                            await PollSwapState([swap.SwapId], cancellationToken);
                            continue;
                        }

                        if (swap.SwapType == ArkSwapType.ChainArkToBtc)
                        {
                            await TryCoopRefundArkToBtc(swap, swapStatus, cancellationToken);
                            continue;
                        }
                        await TryRefundBtcToArk(swap, swapStatus, cancellationToken);
                        continue;
                    }
                    
                    case BoltzSwapAction.CanClaimChain:
                        await TryClaimBtcForChainSwap(swap, cancellationToken);
                        break;
                    
                    case BoltzSwapAction.ReadyToSignClaim:
                        await TrySignBoltzBtcClaim(swap, cancellationToken);
                        break;
                }

                
                // Re-read swap — claim handlers may have updated status to terminal
                var updatedSwaps = await _swapsStorage.GetSwaps(swapIds: [idToPoll], cancellationToken: cancellationToken);
                swap = updatedSwaps.FirstOrDefault() ?? swap;
                if (swap.Status.IsSuccess())
                {
                    continue;
                }

                // Only update status for genuinely terminal Boltz statuses.
                // Operational statuses (swap.expired, invoice.failedToPay, etc.)
                // return null — the classifier above already handled the action.
                var newStatus = BoltzSwapStatus.ToArkSwapStatus(swapStatus.Status);
                if (newStatus is null)
                {
                    _logger?.LogDebug("Swap {SwapId}: Boltz '{BoltzStatus}' is an operational status, no status update",
                        idToPoll, swapStatus.Status);
                    continue;
                }

                if (swap.Status == newStatus)
                {
                    _logger?.LogDebug("Swap {SwapId}: Boltz '{BoltzStatus}' -> {Status}, unchanged",
                        idToPoll, swapStatus.Status, newStatus);
                    continue;
                }

                _logger?.LogInformation("Swap {SwapId}: {OldStatus} -> {NewStatus} (Boltz: '{BoltzStatus}')",
                    idToPoll, swap.Status, newStatus, swapStatus.Status);

                var swapWithNewStatus = swap with { Status = newStatus.Value, UpdatedAt = DateTimeOffset.UtcNow };
                await _swapsStorage.SaveSwap(swap.WalletId, swapWithNewStatus, cancellationToken: cancellationToken);
                RaiseSwapStatusChanged(swapWithNewStatus);

                if (swapWithNewStatus.Status.IsTerminalState())
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
        RaiseSwapStatusChanged(newSwap, newSwap.FailReason);

        _logger?.LogWarning(
            "Swap {SwapId}: marked Failed after {Threshold} consecutive Boltz 404s — swap is unknown to the configured Boltz instance",
            swapId, UnknownToProviderThreshold);

        // Stop polling and clear the counter. NotifySwapChanged handles
        // _scriptToSwapId eviction via the SaveSwap event we just emitted.
        _swapsIdToWatch.TryRemove(swapId, out _);
        _consecutiveUnknown.TryRemove(swapId, out _);
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

    // ─── Swap Creation (delegated from SwapsManagementService) ────

    internal BoltzSwapService BoltzService => _boltzService;

    // ─── Swap Restoration ──────────────────────────────────────────

    internal async Task<RestorableSwap[]> RestoreSwapsFromBoltzAsync(
        string[] publicKeys, CancellationToken ct)
    {
        return (await _boltzClient.RestoreSwapsAsync(publicKeys, ct))
            .Where(swap => swap.From == "ARK" || swap.To == "ARK").ToArray();
    }
}
