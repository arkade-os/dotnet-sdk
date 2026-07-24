using System.Collections.Concurrent;
using System.Text.Json;
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
using NArk.Swaps.Boltz.Models.Swaps.Common;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;

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

    private readonly CancellationTokenSource _shutdownCts = new();
    /// <summary>
    /// Linked CTS produced inside StartAsync that joins the caller's token and our
    /// internal shutdown token. Stored as a field so it gets disposed on shutdown
    /// instead of leaking the registration handle for the provider's lifetime.
    /// </summary>
    private CancellationTokenSource? _linkedStartCts;

    // 1-in 1-out P2TR key-path spend: 94 base vBytes + 67 witness / 4 ≈ 111 vBytes.
    private const int ClaimRefundVBytes = 111;

    private async Task<long> EstimateClaimRefundFeeAsync(CancellationToken ct)
    {
        var feeRate = await _chainTimeProvider.EstimateFeeRateAsync(confirmTarget: 2, ct);
        return (long)feeRate.GetFee(ClaimRefundVBytes).Satoshi;
    }

    /// <summary>
    /// Set of swap ids currently watched on the persistent Boltz websocket.
    /// Concurrent because the websocket loop reads it while reconciliation and
    /// storage events mutate it. Modelled as a dictionary because there is no
    /// <c>ConcurrentHashSet&lt;T&gt;</c>; the byte value is unused.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _swapsIdToWatch = new();
    internal void WatchSwap(string swapId) => _swapsIdToWatch.TryAdd(swapId, 0);

    /// <summary>
    /// Per-swap counter of consecutive <see cref="BoltzSwapNotFoundException"/>
    /// responses. Reset by any successful status response.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _consecutiveUnknown = [];

    /// <summary>
    /// Number of consecutive Boltz 404s before a swap becomes terminal. At the
    /// one-minute polling cadence this gives transient routing failures ten
    /// minutes to recover.
    /// </summary>
    private const int UnknownToProviderThreshold = 10;

    private Task? _routinePollTask;

    /// <summary>
    /// Long-lived task that owns the persistent Boltz websocket connection.
    /// One task serves the provider lifetime; set changes use subscribe and
    /// unsubscribe operations on that connection.
    /// </summary>
    private Task? _websocketTask;
    /// <summary>The currently-connected client, or null when reconnecting / shutting down.</summary>
    private IBoltzWebsocketClient? _websocket;
    internal Func<Uri, IBoltzWebsocketClient> WebsocketClientFactory { get; set; } =
        uri => new BoltzWebsocketClient(uri);
    internal TimeSpan WebsocketReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Serialises subscribe/unsubscribe calls so two storage events firing
    /// at once can't interleave their websocket sends mid-payload.
    /// </summary>
    private readonly SemaphoreSlim _websocketLock = new(1, 1);
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
        _boltzService = new BoltzSwapService(boltzClient, clientTransport, limitsValidator);
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
    /// Called by the router when a VTXO changes. Deliberately a no-op: VTXO
    /// state for a swap's contract script is refreshed from the indexer inside
    /// <see cref="ProcessSwapStatus"/>, which every websocket update and every
    /// reconciliation tick already goes through.
    /// </summary>
    public void NotifyVtxoChanged(ArkVtxo vtxo) { }

    /// <summary>
    /// Called by the router when a swap record changes in storage.
    /// </summary>
    public void NotifySwapChanged(ArkSwap swap)
    {
        if (swap.Status.IsTerminalState())
        {
            if (_swapsIdToWatch.TryRemove(swap.SwapId, out _))
                _ = UpdateWebsocketSubscriptionsAsync([swap.SwapId], subscribe: false);
        }
        else if (_swapsIdToWatch.TryAdd(swap.SwapId, 0))
        {
            _ = UpdateWebsocketSubscriptionsAsync([swap.SwapId], subscribe: true);
        }
    }

    private async Task RoutinePoll(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileActiveSwaps(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Boltz routine reconciliation failed");
            }

            await Task.Delay(interval, cancellationToken);
        }
    }

    internal async Task ReconcileActiveSwaps(CancellationToken cancellationToken)
    {
        var activeSwaps = await _swapsStorage.GetSwaps(active: true, cancellationToken: cancellationToken);
        var activeIds = activeSwaps.Select(s => s.SwapId).ToHashSet();
        var watchedIds = _swapsIdToWatch.Keys.ToHashSet();
        var added = activeIds.Except(watchedIds).ToArray();
        var removed = watchedIds.Except(activeIds).ToArray();

        foreach (var id in added) _swapsIdToWatch.TryAdd(id, 0);
        foreach (var id in removed)
        {
            _swapsIdToWatch.TryRemove(id, out _);
            // A swap that left the active set by any other route would otherwise
            // leave its 404 counter behind for the provider's lifetime.
            _consecutiveUnknown.TryRemove(id, out _);
        }

        // WebSocket acknowledgements must never hold up REST reconciliation.
        if (added.Length > 0)
            _ = UpdateWebsocketSubscriptionsAsync(added, subscribe: true);
        if (removed.Length > 0)
            _ = UpdateWebsocketSubscriptionsAsync(removed, subscribe: false);

        foreach (var chunk in activeIds.Chunk(BoltzClient.MaxSwapStatusBatchSize))
        {
            try
            {
                var statuses = await _boltzClient.GetSwapStatusesAsync(chunk, cancellationToken);
                foreach (var (swapId, status) in statuses)
                {
                    try
                    {
                        await ProcessSwapStatus(swapId, status, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogError(ex, "Swap {SwapId}: reconciliation failed", swapId);
                    }
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Identify the stale ID without penalizing valid swaps in the chunk.
                await PollSwapState(chunk, cancellationToken);
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
                var swapStatus = await _boltzClient.GetSwapStatusAsync(idToPoll, cancellationToken);
                if (swapStatus?.Status is null)
                {
                    _logger?.LogDebug("Swap {SwapId}: Boltz returned null status", idToPoll);
                    continue;
                }
                await ProcessSwapStatus(idToPoll, swapStatus, cancellationToken);
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

    private async Task ProcessSwapStatus(
        string swapId,
        SwapStatusResponse swapStatus,
        CancellationToken cancellationToken)
    {
        _consecutiveUnknown.TryRemove(swapId, out _);
        _logger?.LogInformation("Swap {SwapId}: Boltz status '{BoltzStatus}'", swapId, swapStatus.Status);

        await using var @lock = await _safetyService.LockKeyAsync($"swap::{swapId}", cancellationToken);
        var swap = (await _swapsStorage.GetSwaps(swapIds: [swapId], cancellationToken: cancellationToken))
            .FirstOrDefault();
        if (swap is null || swap.Status.IsTerminalState())
            return;

        // Scope transitive action logs to the owning wallet.
        using var walletScope = _logger?.BeginScope(("WalletId", swap.WalletId));

        // Refresh directly because indexer subscriptions do not replay missed events.
        if (!string.IsNullOrEmpty(swap.ContractScript))
        {
            try
            {
                await foreach (var freshVtxo in _clientTransport.GetVtxoByScriptsAsSnapshot(
                                   new HashSet<string> { swap.ContractScript }, cancellationToken))
                {
                    await _vtxoStorage.UpsertVtxo(freshVtxo, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Swap {SwapId}: failed to refresh its contract VTXOs", swapId);
            }
        }

        switch (BoltzOperationClassifier.Classify(swap, swapStatus.Status))
        {
            case BoltzSwapAction.CanCoopRefundSubmarine:
                await RequestSubmarineCoopRefund(swap, swapStatus, cancellationToken);
                return;
            case BoltzSwapAction.CanCoopRefundArkToBtc:
                await TryCoopRefundArkToBtc(swap, swapStatus, cancellationToken);
                return;
            case BoltzSwapAction.CanCoopRefundBtcToArk:
                await TryRefundBtcToArk(swap, swapStatus, cancellationToken);
                return;
            case BoltzSwapAction.CanRenegotiateChain:
                if (await TryRenegotiateChainSwap(swap, cancellationToken))
                {
                    await PollSwapState([swap.SwapId], cancellationToken);
                    return;
                }
                if (swap.SwapType == ArkSwapType.ChainArkToBtc)
                    await TryCoopRefundArkToBtc(swap, swapStatus, cancellationToken);
                else
                    await TryRefundBtcToArk(swap, swapStatus, cancellationToken);
                return;
            case BoltzSwapAction.CanClaimChain:
                await TryClaimBtcForChainSwap(swap, cancellationToken);
                break;
            case BoltzSwapAction.ReadyToSignClaim:
                await TrySignBoltzBtcClaim(swap, cancellationToken);
                break;
        }

        // Claim and refund handlers may have made the swap terminal.
        swap = (await _swapsStorage.GetSwaps(swapIds: [swapId], cancellationToken: cancellationToken))
            .FirstOrDefault() ?? swap;
        if (swap.Status.IsSuccess())
            return;

        // Operational statuses are handled above and map to null.
        var newStatus = BoltzSwapStatus.ToArkSwapStatus(swapStatus.Status);
        if (newStatus is null || swap.Status == newStatus)
            return;

        var updatedSwap = swap with { Status = newStatus.Value, UpdatedAt = DateTimeOffset.UtcNow };
        await _swapsStorage.SaveSwap(swap.WalletId, updatedSwap, cancellationToken: cancellationToken);
        RaiseSwapStatusChanged(updatedSwap);

        if (updatedSwap.Status.IsTerminalState() &&
            _swapsIdToWatch.TryRemove(updatedSwap.SwapId, out _))
        {
            await UpdateWebsocketSubscriptionsAsync([updatedSwap.SwapId], subscribe: false);
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

        // Stop monitoring and clear the counter.
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
    internal async Task RunWebsocketLoop(CancellationToken cancellationToken)
    {
        var wsUri = _boltzClient.DeriveWebSocketUri();
        while (!cancellationToken.IsCancellationRequested)
        {
            IBoltzWebsocketClient? client = null;
            try
            {
                _logger?.LogInformation("Connecting to Boltz websocket at {Uri}", wsUri);
                client = WebsocketClientFactory(wsUri);
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
                await Task.Delay(WebsocketReconnectDelay, cancellationToken);
        }
    }

    /// <summary>
    /// Subscribe or unsubscribe swap ids on the current persistent websocket.
    /// No-ops when the websocket is disconnected and swallows send failures —
    /// the reconnect loop re-subscribes from <see cref="_swapsIdToWatch"/> on
    /// its next attempt, and leaving a terminal swap subscribed costs only a
    /// stray status update that is ignored locally.
    /// </summary>
    private async Task UpdateWebsocketSubscriptionsAsync(IReadOnlyList<string> swapIds, bool subscribe)
    {
        if (swapIds.Count == 0) return;
        var operation = subscribe ? "Subscribe" : "Unsubscribe";
        var token = _shutdownCts.Token;
        try
        {
            await _websocketLock.WaitAsync(token);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (_websocket is null)
            {
                _logger?.LogDebug(
                    "Skipping websocket {Operation}: connection not yet up; reconnect loop will pick up [{SwapIds}]",
                    operation, string.Join(", ", swapIds));
                return;
            }

            var ids = swapIds.ToArray();
            if (subscribe)
                await _websocket.SubscribeAsync(ids, token);
            else
                await _websocket.UnsubscribeAsync(ids, token);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex,
                "websocket {Operation} failed for [{SwapIds}]; reconnect loop will retry",
                operation, string.Join(", ", swapIds));
        }
        finally
        {
            _websocketLock.Release();
        }
    }

    private async Task OnSwapEventReceived(WebSocketResponse? response)
    {
        try
        {
            if (response is null)
                return;

            if (response.Event == "update" && response is { Channel: "swap.update", Args.Count: > 0 })
            {
                foreach (var swapUpdate in response.Args)
                {
                    var id = swapUpdate?["id"]?.GetValue<string>();
                    var status = swapUpdate?.Deserialize<SwapStatusResponse>();
                    if (id is null || status?.Status is null)
                        continue;

                    try
                    {
                        await ProcessSwapStatus(id, status, _shutdownCts.Token);
                    }
                    catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Swap {SwapId}: WebSocket update failed", id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing websocket event");
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
}
