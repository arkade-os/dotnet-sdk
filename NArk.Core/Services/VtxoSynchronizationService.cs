using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;

namespace NArk.Core.Services;

public class VtxoSynchronizationService : IAsyncDisposable
{
    /// <summary>
    /// Metadata key on <see cref="ArkWalletInfo.Metadata"/> where the
    /// per-wallet "last successful full-set poll" timestamp is stored.
    /// Cold-start catch-up reads <c>MIN</c> across wallets; routine polls
    /// write the same StartedAt to every wallet on success.
    /// </summary>
    public const string LastFullPollAtMetadataKey = "vtxo.lastFullPollAt";
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _queryTask;

    // The long-lived stream supervisor task. It keeps exactly one GetSubscription
    // stream open and updates the watched set IN PLACE (Subscribe/Unsubscribe) rather
    // than tearing the stream down on every script change.
    private Task? _streamTask;
    // Cancelled to interrupt the supervisor's current stream so it re-reads the
    // subscription state (on recreate or teardown). A fresh instance per generation.
    private CancellationTokenSource? _streamGenerationCts;
    // Released when a subscription is (re)created from the idle/no-subscription state,
    // to wake the supervisor out of its idle wait.
    private readonly SemaphoreSlim _streamWakeup = new(0);
    // arkd subscription id for the current in-place subscription; null when there is
    // none (empty active set, or torn down).
    private string? _subscriptionId;

    /// <summary>
    /// The script set the subscription stream is currently subscribed to.
    /// This is bookkeeping for the long-lived gRPC subscription only — it tells
    /// us when to restart the stream (the subscribed set differs from the
    /// freshly-derived active set). It is NOT the source of truth for what to
    /// poll: <see cref="RoutinePoll"/> re-derives the active set fresh from the
    /// providers every tick, so a drift here can never hide a script from
    /// detection — the next poll catches it and re-syncs the stream.
    /// </summary>
    private HashSet<string> _subscribedScripts = [];

    /// <summary>
    /// The scripts the subscription stream is currently subscribed to (for
    /// debugging/observability). The 5-second safety-net poll always operates
    /// on a freshly-derived active set, independent of this value.
    /// </summary>
    public IReadOnlySet<string> ListenedScripts => _subscribedScripts;

    private readonly SemaphoreSlim _viewSyncLock = new(1);

    // Stream-triggered polls only need changes within a short recent window.
    // UpdateScriptsView full-syncs newly-added scripts with After=null.
    private static readonly TimeSpan StreamPollLookback = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Internal poll work item.
    /// </summary>
    /// <param name="Scripts">Scripts to poll on this iteration.</param>
    /// <param name="After">
    /// Lower bound on VTXO last-update for the indexer query. <c>null</c>
    /// means "full history" — used for newly-added scripts.
    /// </param>
    /// <param name="IsFullSetSnapshot">
    /// When true, this poll covers the entire active script view at the time
    /// of enqueuing, and on success the per-wallet
    /// <see cref="LastFullPollAtMetadataKey"/> entries advance to
    /// <see cref="StartedAt"/>. Stream-driven and newly-added-scripts polls
    /// set this false.
    /// </param>
    /// <param name="StartedAt">
    /// Wall-clock time the request was created. On a successful full-set
    /// poll this becomes the new per-wallet
    /// <see cref="LastFullPollAtMetadataKey"/> value — using "started"
    /// not "completed" guarantees that any change which lands on arkd while
    /// our poll is in flight is still inside the next poll's <c>after</c> window.
    /// </param>
    /// <param name="IsColdStartCatchup">
    /// Marks the very first cold-start catch-up poll. On its successful
    /// completion <c>_coldStartCatchupComplete</c> flips to true, ungating
    /// the persistent cursor advance for subsequent <see cref="IsFullSetSnapshot"/>
    /// polls. Until that happens, a failure-then-success sequence
    /// (catch-up fails, routine poll succeeds) MUST NOT advance the cursor —
    /// otherwise the window between the stored cursor and the routine
    /// poll's lookback is permanently skipped.
    /// </param>
    private readonly record struct PollRequest(
        HashSet<string> Scripts,
        DateTimeOffset? After,
        bool IsFullSetSnapshot = false,
        DateTimeOffset StartedAt = default,
        bool IsColdStartCatchup = false);

    // Unbounded: retry schedules + RoutinePoll + catchup can all enqueue at once,
    // and we never want stream-event processing to block on back-pressure. The
    // sequential reader (StartQueryLogic) drains in order and upserts are idempotent.
    private readonly Channel<PollRequest> _readyToPoll =
        Channel.CreateUnbounded<PollRequest>();

    private readonly IVtxoStorage _vtxoStorage;
    private readonly IClientTransport _arkClientTransport;
    private readonly IEnumerable<IActiveScriptsProvider> _activeScriptsProviders;
    private readonly IWalletStorage? _walletStorage;
    private readonly ILogger<VtxoSynchronizationService>? _logger;

    /// <summary>
    /// Set on startup, cleared after the first <see cref="UpdateScriptsView"/>
    /// initial-catchup poll. While true, that initial poll reads the per-wallet
    /// <see cref="LastFullPollAtMetadataKey"/> entries (taking <c>MIN</c>) as
    /// its <c>after</c> filter so wallets with long history don't refetch every
    /// VTXO on every cold start.
    /// </summary>
    private bool _isFirstStartupCatchup = true;

    /// <summary>
    /// Gate on writing the per-wallet <see cref="LastFullPollAtMetadataKey"/>
    /// entries. Stays false until the cold-start catch-up poll succeeds at
    /// least once; while false, even successful
    /// <see cref="PollRequest.IsFullSetSnapshot"/> polls (i.e. routine polls)
    /// leave the stored cursor untouched. This prevents a transient catch-up
    /// failure followed by a routine-poll success from advancing the cursor
    /// past the catch-up window — which would permanently skip any VTXO that
    /// landed during the downtime. Initialised to true when no
    /// <see cref="IWalletStorage"/> is wired, since there is nothing to gate.
    /// </summary>
    private volatile bool _coldStartCatchupComplete;

    public VtxoSynchronizationService(
        IEnumerable<IActiveScriptsProvider> activeScriptsProviders,
        IVtxoStorage vtxoStorage,
        IClientTransport arkClientTransport,
        ILogger<VtxoSynchronizationService> logger,
        IWalletStorage? walletStorage = null)
        : this(vtxoStorage, arkClientTransport, activeScriptsProviders, walletStorage)
    {
        _logger = logger;
    }

    public VtxoSynchronizationService(
        IVtxoStorage vtxoStorage,
        IClientTransport arkClientTransport,
        IEnumerable<IActiveScriptsProvider> activeScriptsProviders,
        IWalletStorage? walletStorage = null)
    {
        _vtxoStorage = vtxoStorage;
        _arkClientTransport = arkClientTransport;
        _activeScriptsProviders = activeScriptsProviders;
        _walletStorage = walletStorage;
        // Without a wallet storage there is no cursor to advance, so the
        // gate is irrelevant — start in the "complete" state to keep the
        // opt-out path identical to pre-cursor behaviour.
        _coldStartCatchupComplete = walletStorage is null;

        foreach (var provider in _activeScriptsProviders)
        {
            provider.ActiveScriptsChanged += OnActiveScriptsChanged;
        }

        // Subscribe to VTXO changes for auto-deactivation of awaiting contracts
        _vtxoStorage.VtxosChanged += OnVtxoReceived;
    }

    private async void OnVtxoReceived(object? sender, ArkVtxo vtxo)
    {
        try
        {
            await HandleContractStateTransitionsForScript(vtxo.Script);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Error handling contract state transitions for script {Script}", vtxo.Script);
        }
    }

    private async Task HandleContractStateTransitionsForScript(string script)
    {
        // Find all contract storages and handle state transitions
        foreach (var provider in _activeScriptsProviders)
        {
            if (provider is IContractStorage contractStorage)
            {
                // Deactivate contracts that are awaiting funds before deactivation (one-time-use contracts)
                var deactivatedCount = await contractStorage.DeactivateAwaitingContractsByScript(script, _shutdownCts.Token);
                if (deactivatedCount > 0)
                {
                    _logger?.LogInformation("Auto-deactivated {Count} awaiting contracts for script {Script}", deactivatedCount, script);
                }
            }
        }
    }

    private async void OnActiveScriptsChanged(object? sender, EventArgs e)
    {
        try
        {
            await UpdateScriptsView(_shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            var senderStr = sender?.GetType().Name ?? "";
            _logger?.LogDebug($"Active Script handler {senderStr} cancelled");
        }
        catch (Exception ex)
        {
            var senderStr = sender?.GetType().Name ?? "";
            _logger?.LogWarning(0, ex, $"Error handling active scripts change event from {senderStr}");
        }
    }

    // Safety-net periodic poll. arkd's script subscription has been observed to
    // silently miss VTXO events for scripts that were added to the subscription
    // after the stream opened, and even when the event does fire, arkd's indexer
    // has been seen to take 10-30s to make the VTXO queryable. The 5-second tick
    // with a 2-minute `after` window bounds detection latency while staying
    // cheap (each tick is one gRPC call with a small result set).
    // Tunable for tests via the internal init property.
    internal TimeSpan RoutinePollInterval { get; init; } = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RoutinePollLookback = TimeSpan.FromMinutes(2);
    // Backoff before reconnecting the subscription stream after a transient fault.
    private static readonly TimeSpan StreamReconnectDelay = TimeSpan.FromSeconds(1);
    private Task? _routinePollTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting VTXO synchronization service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        _queryTask = StartQueryLogic(multiToken.Token);
        _routinePollTask = RoutinePoll(multiToken.Token);
        // The supervisor starts idle and wakes once UpdateScriptsView creates the
        // initial subscription below.
        _streamTask = RunStreamSupervisorAsync(multiToken.Token);
        await UpdateScriptsView(multiToken.Token);
    }

    private async Task RoutinePoll(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RoutinePollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // Derive the active-script set FRESH from the providers every
                // tick (provider-agnostic — we don't care whether a script comes
                // from a contract or an existing VTXO). This is the drift-proof
                // source of truth for what to poll: a stale or missed stream
                // subscription can never hide a script from detection, because
                // the next tick re-derives and polls it regardless. A single
                // periodic derivation is cheap — the historical 11k×11k blow-up
                // came from firing the change event per VTXO upsert, not from
                // one periodic query.
                var scripts = await GatherActiveScriptsAsync(cancellationToken);
                if (scripts.Count == 0)
                    continue;

                // Keep the subscription stream in sync with reality. If the
                // freshly derived set differs from what the stream is subscribed
                // to, refresh — UpdateScriptsView restarts the stream and runs a
                // full-history catch-up for newly-added scripts, recovering any
                // VTXO that landed while a script was unsubscribed.
                if (!scripts.SetEquals(_subscribedScripts))
                {
                    _logger?.LogInformation(
                        "RoutinePoll: active set ({Count}) differs from the stream subscription ({Subscribed}) — refreshing subscription",
                        scripts.Count, _subscribedScripts.Count);
                    await UpdateScriptsView(cancellationToken);
                }

                var startedAt = DateTimeOffset.UtcNow;
                var after = startedAt - RoutinePollLookback;
                _logger?.LogDebug(
                    "RoutinePoll: re-polling {Count} active script(s) with after={After}",
                    scripts.Count, after.ToString("O"));
                // IsFullSetSnapshot=true: on success the StartedAt timestamp will
                // be persisted to every wallet's vtxo.lastFullPollAt metadata,
                // bounding the next cold-start catch-up window.
                await _readyToPoll.Writer.WriteAsync(
                    new PollRequest(scripts, after, IsFullSetSnapshot: true, StartedAt: startedAt),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "RoutinePoll: failed to enqueue safety-net poll; continuing");
            }
        }
    }

    /// <summary>
    /// Derives the current active-script set by unioning every
    /// <see cref="IActiveScriptsProvider"/>. Provider-agnostic: it does not care
    /// whether a script is backed by a contract or an existing VTXO. A failing
    /// provider is logged and skipped rather than aborting the whole refresh —
    /// one storage hiccup must not blank the set and tear down the subscription
    /// for every other provider's scripts; the next derivation re-includes it.
    /// </summary>
    private async Task<HashSet<string>> GatherActiveScriptsAsync(CancellationToken token)
    {
        var result = new HashSet<string>();
        foreach (var provider in _activeScriptsProviders)
        {
            try
            {
                result.UnionWith(await provider.GetActiveScripts(token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "GetActiveScripts failed for provider {Provider}; skipping it for this refresh",
                    provider.GetType().Name);
            }
        }
        return result;
    }

    private async Task UpdateScriptsView(CancellationToken token)
    {
        await _viewSyncLock.WaitAsync(token);
        try
        {
            var newView = await GatherActiveScriptsAsync(token);

            // Empty active set → disconnect stream and go idle.
            if (newView.Count == 0)
            {
                if (_subscribedScripts.Count > 0)
                {
                    _logger?.LogInformation("UpdateScriptsView: active set empty — disconnecting stream");
                    _subscriptionId = null;
                    SignalStreamGenerationChange();
                }
                _subscribedScripts = [];
                return;
            }

            // Transitioning from empty → first scripts: supervisor is idle, wake it.
            if (_subscribedScripts.Count == 0)
            {
                _subscribedScripts = newView;
                _logger?.LogInformation("UpdateScriptsView: first {Count} script(s) — waking supervisor", newView.Count);
                await EnqueueCatchupPollAsync(newView, token);
                SignalStreamWakeup();
                return;
            }

            var added = newView.Except(_subscribedScripts).ToHashSet();
            var removed = _subscribedScripts.Except(newView).ToHashSet();
            if (added.Count == 0 && removed.Count == 0)
            {
                _logger?.LogDebug("UpdateScriptsView: unchanged ({Count} scripts)", newView.Count);
                return;
            }

            // Live subscription → update in place via UpdateSubscription (no stream restart).
            if (_subscriptionId is not null)
            {
                _logger?.LogInformation(
                    "UpdateScriptsView: in-place update +{Added}/-{Removed} (now {Count}) on subscription {Id}",
                    added.Count, removed.Count, newView.Count, _subscriptionId);
                try
                {
                    await _arkClientTransport.UpdateSubscriptionScriptsAsync(
                        _subscriptionId,
                        added.Count > 0 ? added : null,
                        removed.Count > 0 ? removed : null,
                        token);
                    if (added.Count > 0)
                        await EnqueueCatchupPollAsync(added, token);
                    _subscribedScripts = newView;
                    return;
                }
                catch (Exception ex) when (IsSubscriptionNotFound(ex))
                {
                    // arkd GC'd the subscription. Clear the ID and let the supervisor reopen fresh.
                    _logger?.LogInformation("UpdateScriptsView: subscription {Id} GC'd — supervisor will reopen", _subscriptionId);
                    _subscriptionId = null;
                    _subscribedScripts = newView;
                    if (added.Count > 0)
                        await EnqueueCatchupPollAsync(added, token);
                    SignalStreamGenerationChange();
                    return;
                }
            }

            // No subscription ID yet (supervisor is connecting). Update the target script set
            // and interrupt it so it reconnects with the new set as initial filter.
            _logger?.LogInformation(
                "UpdateScriptsView: script set changed while supervisor is connecting — restarting with {Count} scripts",
                newView.Count);
            _subscribedScripts = newView;
            SignalStreamGenerationChange();
        }
        finally
        {
            _viewSyncLock.Release();
        }
    }

    /// <summary>
    /// Enqueues a catch-up poll for <paramref name="scripts"/>, recovering any VTXO that
    /// landed before these scripts were being watched. The first call after startup is the
    /// cold-start catch-up: it reads MIN(per-wallet vtxo.lastFullPollAt) as its <c>after</c>
    /// filter (so wallets with long history don't refetch everything) and advances the
    /// persisted cursor on success. Every later call is a full-history fetch
    /// (<c>after = null</c>). Caller holds <see cref="_viewSyncLock"/>.
    /// </summary>
    private async Task EnqueueCatchupPollAsync(HashSet<string> scripts, CancellationToken token)
    {
        if (scripts.Count == 0)
            return;

        DateTimeOffset? catchupAfter = null;
        var isInitialCatchup = _isFirstStartupCatchup;
        if (isInitialCatchup && _walletStorage is not null)
        {
            try
            {
                catchupAfter = await ReadCursorMinAcrossWalletsAsync(token);
                if (catchupAfter is not null)
                    _logger?.LogInformation(
                        "First-startup catch-up: using MIN(per-wallet {Key})={After} as `after` filter for {Count} script(s)",
                        LastFullPollAtMetadataKey, catchupAfter.Value.ToString("O"), scripts.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "First-startup catch-up: failed to read per-wallet {Key}; falling back to full-history fetch",
                    LastFullPollAtMetadataKey);
            }
        }
        _isFirstStartupCatchup = false;

        // The cold-start catch-up is a full-set snapshot: on success the gate flips and the
        // per-wallet cursor advances to StartedAt. Later catch-ups (script set grew) stay
        // IsFullSetSnapshot=false and full-history (catchupAfter=null).
        var startedAt = isInitialCatchup ? DateTimeOffset.UtcNow : default;
        await _readyToPoll.Writer.WriteAsync(
            new PollRequest(scripts, catchupAfter, IsFullSetSnapshot: isInitialCatchup,
                StartedAt: startedAt, IsColdStartCatchup: isInitialCatchup),
            token);
    }

    /// <summary>
    /// The long-lived stream supervisor. On each loop iteration it opens a <c>GetSubscription</c>
    /// stream — fresh (no ID, initial scripts in filter) when starting or after a GC, or as a
    /// reconnect (existing ID) after a transient drop. The server sends
    /// <see cref="VtxoSubscriptionStarted"/> as the first event on a new subscription, which
    /// is stored so <see cref="UpdateScriptsView"/> can call <c>UpdateSubscription</c> in place.
    /// </summary>
    private async Task RunStreamSupervisorAsync(CancellationToken shutdownToken)
    {
        while (!shutdownToken.IsCancellationRequested)
        {
            HashSet<string>? initialScripts = null;
            string? reconnectId = null;
            var streamToken = CancellationToken.None;

            await _viewSyncLock.WaitAsync(shutdownToken);
            try
            {
                if (_subscribedScripts.Count > 0)
                {
                    _streamGenerationCts?.Dispose();
                    _streamGenerationCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                    streamToken = _streamGenerationCts.Token;
                    reconnectId = _subscriptionId;
                    // Pass initial scripts only when opening fresh (no reconnect ID).
                    if (reconnectId is null)
                        initialScripts = [.._subscribedScripts];
                }
            }
            finally
            {
                _viewSyncLock.Release();
            }

            if (initialScripts is null && reconnectId is null)
            {
                // Idle: no scripts to watch. Wait until scripts are added.
                try { await _streamWakeup.WaitAsync(shutdownToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var endedGracefully = false;
            try
            {
                _logger?.LogInformation("VTXO subscription stream opening (id={Id})", reconnectId ?? "new");

                await foreach (var ev in _arkClientTransport.OpenSubscriptionStreamAsync(
                    initialScripts, reconnectId, streamToken))
                {
                    switch (ev)
                    {
                        case VtxoSubscriptionStarted started:
                            await _viewSyncLock.WaitAsync(shutdownToken);
                            try { _subscriptionId = started.SubscriptionId; }
                            finally { _viewSyncLock.Release(); }
                            _logger?.LogInformation("VTXO subscription started (id={Id})", started.SubscriptionId);
                            break;

                        case VtxoScriptsChanged changed:
                            _logger?.LogInformation(
                                "VTXO subscription stream: arkd pushed update for {Count} script(s): [{Scripts}]",
                                changed.Scripts.Count, string.Join(", ", changed.Scripts));
                            try
                            {
                                var after = DateTimeOffset.UtcNow - StreamPollLookback;
                                await _readyToPoll.Writer.WriteAsync(new PollRequest(changed.Scripts, after), streamToken);
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Stream push: failed to enqueue immediate poll");
                            }
                            break;
                    }
                }
                endedGracefully = true;
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Generation change (script set update or teardown) — re-read state on next loop.
                continue;
            }
            catch (Exception ex) when (IsSubscriptionNotFound(ex))
            {
                // Subscription was GC'd by arkd. Clear the ID so next loop opens fresh.
                _logger?.LogInformation("VTXO subscription {Id} GC'd — reopening fresh", reconnectId);
                await _viewSyncLock.WaitAsync(shutdownToken);
                try { _subscriptionId = null; }
                finally { _viewSyncLock.Release(); }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "VTXO subscription stream faulted — reconnecting");
                try { await Task.Delay(StreamReconnectDelay, shutdownToken); }
                catch (OperationCanceledException) { return; }
            }

            if (endedGracefully)
                _logger?.LogWarning("VTXO subscription stream ended (arkd closed it) — reconnecting");
        }
    }

    // Wake the supervisor out of its idle wait after a subscription is created from empty.
    private void SignalStreamWakeup()
    {
        if (_streamWakeup.CurrentCount == 0)
            _streamWakeup.Release();
    }

    // Interrupt the supervisor's current stream so it re-reads the subscription state
    // (used on recreate and teardown — the supervisor is streaming, not idle).
    private void SignalStreamGenerationChange()
    {
        try { _streamGenerationCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // arkd reports a missing subscription as "subscription <id> not found" (gRPC Internal,
    // surfaced verbatim over REST too). Matched by message since there's no dedicated code.
    private static bool IsSubscriptionNotFound(Exception ex)
        => ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
           || (ex.InnerException is { } inner && inner.Message.Contains("not found", StringComparison.OrdinalIgnoreCase));

    private async Task? StartQueryLogic(CancellationToken cancellationToken)
    {
        // Per-iteration try/catch: the transport or storage can throw transiently
        // (arkd restart, DB timeout, etc.). We MUST NOT let the whole loop die,
        // otherwise every subsequent stream event writes to _readyToPoll and
        // nothing reads — VTXO detection goes permanently silent until the
        // service is recycled.
        await foreach (var request in _readyToPoll.Reader.ReadAllAsync(cancellationToken))
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                // Pre-poll line is just "we're about to call arkd" — same
                // info is in the result line below, so keep it at Debug to
                // avoid doubling the per-tick spam on the 5-second safety
                // net.
                _logger?.LogDebug(
                    "StartQueryLogic: polling {Count} script(s) (after={After}): [{Scripts}]",
                    request.Scripts.Count,
                    request.After?.ToString("O") ?? "<none>",
                    string.Join(", ", request.Scripts));
                var found = 0;
                await foreach (var vtxo in _arkClientTransport.GetVtxoByScriptsAsSnapshot(
                                   request.Scripts, request.After, before: null, cancellationToken))
                {
                    found++;
                    await _vtxoStorage.UpsertVtxo(vtxo, cancellationToken);
                }
                // The productive case (found > 0 = a VTXO landed) is what
                // operators want to see at Info. The cold-start catch-up
                // also stays at Info even on 0 — it's a one-off first-tick
                // signal worth seeing. Routine 5-second ticks that find
                // nothing drop to Debug so they don't drown the log.
                if (found > 0 || request.IsColdStartCatchup)
                {
                    _logger?.LogInformation(
                        "StartQueryLogic: poll returned {Found} VTXO(s) across {Count} script(s) in {Elapsed}ms",
                        found, request.Scripts.Count, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
                }
                else
                {
                    _logger?.LogDebug(
                        "StartQueryLogic: poll returned 0 VTXO(s) across {Count} script(s) in {Elapsed}ms",
                        request.Scripts.Count, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
                }

                // Mark the cold-start catch-up as complete on its first
                // successful poll. Until this flips, routine polls below
                // are gated from advancing the cursor — protecting against
                // the failure-then-success gap-loss scenario.
                if (request.IsColdStartCatchup)
                {
                    _coldStartCatchupComplete = true;
                }

                // Advance the per-wallet full-poll cursor only after a successful
                // poll that was enqueued as a full-set snapshot AND the cold-start
                // catch-up has succeeded at least once. Per-script and stream-driven
                // polls never advance it.
                if (request.IsFullSetSnapshot && _coldStartCatchupComplete && _walletStorage is not null)
                {
                    try
                    {
                        await WriteCursorAcrossWalletsAsync(request.StartedAt, cancellationToken);
                    }
                    catch (Exception persistEx)
                    {
                        _logger?.LogWarning(persistEx,
                            "Failed to persist per-wallet {Key}={At}; cold-start catch-up will fall back to a longer window",
                            LastFullPollAtMetadataKey, request.StartedAt.ToString("O"));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(0, ex,
                    "StartQueryLogic: poll failed for {Count} script(s) after {Elapsed}ms; continuing loop",
                    request.Scripts.Count, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
            }
        }
    }

    /// <summary>
    /// Reads <c>MIN(parsed-timestamp)</c> across every wallet's
    /// <see cref="LastFullPollAtMetadataKey"/> entry. Returns <c>null</c> if
    /// any wallet has no cursor yet (so a fresh wallet forces a full-history
    /// catch-up rather than skipping its window via someone else's cursor),
    /// or if there are no wallets at all.
    /// </summary>
    private async Task<DateTimeOffset?> ReadCursorMinAcrossWalletsAsync(CancellationToken cancellationToken)
    {
        if (_walletStorage is null) return null;
        var wallets = await _walletStorage.LoadAllWallets(cancellationToken);
        if (wallets.Count == 0) return null;

        DateTimeOffset? minCursor = null;
        foreach (var w in wallets)
        {
            if (w.Metadata is null ||
                !w.Metadata.TryGetValue(LastFullPollAtMetadataKey, out var raw) ||
                string.IsNullOrEmpty(raw))
            {
                // A wallet without a cursor must trigger full-history catch-up —
                // its first-time scripts have no upper bound that can be safely
                // skipped. Bail to null.
                return null;
            }
            if (!DateTimeOffset.TryParse(
                    raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                _logger?.LogWarning(
                    "Wallet {WalletId}: unparseable {Key}={Raw}; falling back to full-history catch-up",
                    w.Id, LastFullPollAtMetadataKey, raw);
                return null;
            }
            if (minCursor is null || parsed < minCursor.Value)
                minCursor = parsed;
        }
        return minCursor;
    }

    /// <summary>
    /// Writes <paramref name="value"/> to every wallet's
    /// <see cref="LastFullPollAtMetadataKey"/> entry. Per-wallet failures are
    /// logged and skipped — one wallet's storage hiccup shouldn't block the
    /// rest of the cohort from advancing.
    /// </summary>
    private async Task WriteCursorAcrossWalletsAsync(DateTimeOffset value, CancellationToken cancellationToken)
    {
        if (_walletStorage is null) return;
        var iso = value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var wallets = await _walletStorage.LoadAllWallets(cancellationToken);
        foreach (var w in wallets)
        {
            try
            {
                await _walletStorage.SetMetadataValue(w.Id, LastFullPollAtMetadataKey, iso, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex,
                    "Wallet {WalletId}: failed to persist {Key}={Iso}; will retry on next routine poll",
                    w.Id, LastFullPollAtMetadataKey, iso);
            }
        }
    }

    /// <summary>
    /// On-demand polling for specific scripts. Use this to poll inactive contract scripts
    /// or any other scripts that aren't actively tracked.
    /// </summary>
    public Task<int> PollScriptsForVtxos(IReadOnlySet<string> scripts, CancellationToken cancellationToken = default)
        => PollScriptsForVtxos(scripts, after: null, cancellationToken);

    /// <summary>
    /// On-demand polling for specific scripts, optionally restricted to VTXOs updated
    /// after the given timestamp. Use an <paramref name="after"/> value with a small
    /// buffer (e.g. <c>UtcNow - 5 minutes</c>) for post-operation catch-up to avoid
    /// re-fetching the full VTXO history of scripts that already have many entries.
    /// </summary>
    /// <param name="scripts">Contract scripts to poll.</param>
    /// <param name="after">Optional lower bound on VTXO last-update timestamp. <c>null</c> returns everything.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of VTXOs returned by arkd (pre-upsert).</returns>
    public async Task<int> PollScriptsForVtxos(IReadOnlySet<string> scripts, DateTimeOffset? after, CancellationToken cancellationToken = default)
    {
        if (scripts.Count == 0)
            return 0;

        _logger?.LogInformation("PollScriptsForVtxos: querying arkd indexer for {Count} scripts (after={After}): [{Scripts}]",
            scripts.Count, after?.ToString("O") ?? "<none>", string.Join(", ", scripts));

        // Debug aid: equivalent REST API URL for manual testing (substitute your arkd host:port).
        // Kept at Debug — at Info this fires on every poll cycle (default 5s) and floods the log
        // with the same query whether VTXOs landed or not.
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            var queryParams = string.Join("&", scripts.Select(s => $"scripts={Uri.EscapeDataString(s)}"));
            if (after.HasValue)
                queryParams += $"&after={after.Value.ToUnixTimeMilliseconds()}";
            _logger.LogDebug("PollScriptsForVtxos: curl http://localhost:7070/v1/indexer/vtxos?{QueryParams}", queryParams);
        }

        var count = 0;

        await foreach (var vtxo in _arkClientTransport.GetVtxoByScriptsAsSnapshot(scripts, after, before: null, cancellationToken))
        {
            count++;
            _logger?.LogInformation("PollScriptsForVtxos: got VTXO {Outpoint} script={Script} spent={IsSpent}",
                vtxo.OutPoint, vtxo.Script, vtxo.SpentByTransactionId != null);
            await _vtxoStorage.UpsertVtxo(vtxo, cancellationToken);
        }

        _logger?.LogInformation("PollScriptsForVtxos: done, {Count} VTXOs returned from arkd", count);
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        _logger?.LogDebug("Disposing VTXO synchronization service");
        await _shutdownCts.CancelAsync();

        _vtxoStorage.VtxosChanged -= OnVtxoReceived;

        foreach (var provider in _activeScriptsProviders)
        {
            provider.ActiveScriptsChanged -= OnActiveScriptsChanged;
        }
        try
        {
            if (_queryTask is not null)
                await _queryTask;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Query task cancelled during disposal");
        }
        try
        {
            if (_streamTask is not null)
                await _streamTask;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Stream task cancelled during disposal");
        }
        try
        {
            if (_routinePollTask is not null)
                await _routinePollTask;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Routine poll task cancelled during disposal");
        }

        _streamGenerationCts?.Dispose();
        _streamWakeup.Dispose();

        _logger?.LogInformation("VTXO synchronization service disposed");
    }
}