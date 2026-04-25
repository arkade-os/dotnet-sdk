using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Sync;
using NArk.Abstractions.VTXOs;
using NArk.Core.Transport;

namespace NArk.Core.Services;

public class VtxoSynchronizationService : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _queryTask;

    private CancellationTokenSource? _restartCts;
    private Task? _streamTask;

    private HashSet<string> _lastViewOfScripts = [];

    /// <summary>
    /// Gets the set of scripts currently being listened to for VTXO updates.
    /// This is useful for debugging to see which contracts are actively tracked.
    /// </summary>
    public IReadOnlySet<string> ListenedScripts => _lastViewOfScripts;

    private readonly SemaphoreSlim _viewSyncLock = new(1);

    // Stream-triggered polls only need changes within a short recent window.
    // UpdateScriptsView full-syncs newly-added scripts with After=null.
    private static readonly TimeSpan StreamPollLookback = TimeSpan.FromMinutes(5);

    // Delays between arkd's subscription push and our follow-up indexer poll(s).
    // arkd can emit the event before the indexer query path has the VTXO visible,
    // and we've observed the commit lag range from <1s to ~30s. Rather than pick a
    // single tuned delay, enqueue a short retry schedule; each poll uses an `after`
    // window so repeated fetches of unchanged data are no-ops in the upsert path.
    private static readonly TimeSpan[] StreamPushPollSchedule =
    [
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(8),
    ];

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
    /// of enqueuing, and on success <see cref="ISyncStateStorage.SetLastFullPollAtAsync"/>
    /// will be advanced to <see cref="StartedAt"/>. Stream-driven and
    /// newly-added-scripts polls set this false.
    /// </param>
    /// <param name="StartedAt">
    /// Wall-clock time the request was created. On a successful full-set
    /// poll this becomes the new <c>LastFullPollAt</c> — using "started"
    /// not "completed" guarantees that any change which lands on arkd while
    /// our poll is in flight is still inside the next poll's <c>after</c> window.
    /// </param>
    private readonly record struct PollRequest(
        HashSet<string> Scripts,
        DateTimeOffset? After,
        bool IsFullSetSnapshot = false,
        DateTimeOffset StartedAt = default);

    // Unbounded: retry schedules + RoutinePoll + catchup can all enqueue at once,
    // and we never want stream-event processing to block on back-pressure. The
    // sequential reader (StartQueryLogic) drains in order and upserts are idempotent.
    private readonly Channel<PollRequest> _readyToPoll =
        Channel.CreateUnbounded<PollRequest>();

    private readonly IVtxoStorage _vtxoStorage;
    private readonly IClientTransport _arkClientTransport;
    private readonly IEnumerable<IActiveScriptsProvider> _activeScriptsProviders;
    private readonly ISyncStateStorage? _syncStateStorage;
    private readonly ILogger<VtxoSynchronizationService>? _logger;

    /// <summary>
    /// Set on startup, cleared after the first <see cref="UpdateScriptsView"/>
    /// initial-catchup poll. While true, that initial poll uses the persisted
    /// <c>LastFullPollAt</c> as its <c>after</c> filter (instead of <c>null</c>)
    /// so wallets with long history don't refetch every VTXO on every cold start.
    /// </summary>
    private bool _isFirstStartupCatchup = true;

    public VtxoSynchronizationService(
        IEnumerable<IActiveScriptsProvider> activeScriptsProviders,
        IVtxoStorage vtxoStorage,
        IClientTransport arkClientTransport,
        ILogger<VtxoSynchronizationService> logger,
        ISyncStateStorage? syncStateStorage = null)
        : this(vtxoStorage, arkClientTransport, activeScriptsProviders, syncStateStorage)
    {
        _logger = logger;
    }

    public VtxoSynchronizationService(
        IVtxoStorage vtxoStorage,
        IClientTransport arkClientTransport,
        IEnumerable<IActiveScriptsProvider> activeScriptsProviders,
        ISyncStateStorage? syncStateStorage = null)
    {
        _vtxoStorage = vtxoStorage;
        _arkClientTransport = arkClientTransport;
        _activeScriptsProviders = activeScriptsProviders;
        _syncStateStorage = syncStateStorage;

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
    private static readonly TimeSpan RoutinePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RoutinePollLookback = TimeSpan.FromMinutes(2);
    private Task? _routinePollTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting VTXO synchronization service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        _queryTask = StartQueryLogic(multiToken.Token);
        _routinePollTask = RoutinePoll(multiToken.Token);
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
                var scripts = _lastViewOfScripts;
                if (scripts.Count == 0)
                    continue;

                var startedAt = DateTimeOffset.UtcNow;
                var after = startedAt - RoutinePollLookback;
                _logger?.LogDebug(
                    "RoutinePoll: re-polling {Count} active script(s) with after={After}",
                    scripts.Count, after.ToString("O"));
                // IsFullSetSnapshot=true: on success the StartedAt timestamp will
                // be persisted as LastFullPollAt, bounding the next cold-start
                // catch-up window.
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

    private async Task UpdateScriptsView(CancellationToken token)
    {
        await _viewSyncLock.WaitAsync(token);
        try
        {
            var newViewOfScripts = (await Task.WhenAll(_activeScriptsProviders.Select(p => p.GetActiveScripts(token)))).SelectMany(c => c).ToHashSet();

            if (newViewOfScripts.Count == 0)
                return;

            // We already have a stream with this exact script list
            if (newViewOfScripts.SetEquals(_lastViewOfScripts) && _streamTask is not null && !_streamTask.IsCompleted)
            {
                _logger?.LogDebug("UpdateScriptsView: unchanged ({Count} scripts), skipping stream restart", newViewOfScripts.Count);
                return;
            }

            var newlyAdded = newViewOfScripts.Except(_lastViewOfScripts).ToHashSet();
            _logger?.LogInformation("UpdateScriptsView: script set changed from {OldCount} to {NewCount} scripts, restarting stream. New scripts: [{NewScripts}]",
                _lastViewOfScripts.Count, newViewOfScripts.Count,
                string.Join(", ", newlyAdded));

            try
            {
                if (_restartCts is not null)
                    await _restartCts.CancelAsync();
                if (_streamTask is not null)
                    await _streamTask;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(0, ex, "Error cancelling previous stream during scripts view update");
            }

            _lastViewOfScripts = newViewOfScripts;
            _restartCts = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
            // Start a new subscription stream for the whole view.
            _streamTask = StartStreamLogic(newViewOfScripts, _restartCts.Token);
            // Catch-up poll: only newly-added scripts need a full-history fetch
            // (already-known scripts have been synced and will receive stream
            // events for future changes). Skip when the set only shrank.
            if (newlyAdded.Count > 0)
            {
                // First-startup nuance: at this point _lastViewOfScripts WAS
                // empty (we're populating it from cold), so "newly added" =
                // entire set. Without the persisted LastFullPollAt cursor we'd
                // re-fetch every script's full VTXO history every cold start.
                // Use the stored timestamp as an `after` filter on this one
                // call so the cold-start catch-up window equals "since last
                // shutdown" rather than "all of history".
                DateTimeOffset? catchupAfter = null;
                if (_isFirstStartupCatchup && _syncStateStorage is not null)
                {
                    try
                    {
                        catchupAfter = await _syncStateStorage.GetLastFullPollAtAsync(token);
                        if (catchupAfter is not null)
                        {
                            _logger?.LogInformation(
                                "First-startup catch-up: using stored LastFullPollAt={After} as `after` filter for {Count} script(s)",
                                catchupAfter.Value.ToString("O"), newlyAdded.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex,
                            "First-startup catch-up: failed to read LastFullPollAt; falling back to full-history fetch");
                    }
                }
                _isFirstStartupCatchup = false;

                await _readyToPoll.Writer.WriteAsync(new PollRequest(newlyAdded, catchupAfter), token);
            }
        }
        finally
        {
            _viewSyncLock.Release();
        }
    }

    private async Task StartStreamLogic(HashSet<string> scripts, CancellationToken token)
    {
        _logger?.LogInformation(
            "VTXO subscription stream starting for {ScriptCount} script(s)", scripts.Count);
        var endedGracefully = false;
        try
        {
            var restartableToken =
                CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
            await foreach (var vtxosToPoll in _arkClientTransport.GetVtxoToPollAsStream(scripts, restartableToken.Token))
            {
                _logger?.LogInformation(
                    "VTXO subscription stream: arkd pushed update for {Count} script(s): [{Scripts}]",
                    vtxosToPoll.Count, string.Join(", ", vtxosToPoll));
                // arkd's subscription event can fire well before its indexer query path
                // has committed the new VTXO — we've observed anywhere from <1s to
                // ~30s. Fire a short schedule of retry polls rather than relying on a
                // single tuned delay. All polls use an `after` window so repeated
                // fetches of unchanged state are no-ops on the upsert side.
                _ = FirePollSchedule(vtxosToPoll, restartableToken.Token);
            }
            endedGracefully = true;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger?.LogWarning(0, ex, "VTXO subscription stream failed — restarting scripts view");
            await UpdateScriptsView(_shutdownCts.Token);
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(0, ex, "VTXO subscription stream cancelled");
            return;
        }

        // Graceful end: arkd closed the stream without an error. We must restart
        // or we silently lose every subsequent VTXO notification for these scripts.
        if (endedGracefully && !token.IsCancellationRequested)
        {
            _logger?.LogWarning(
                "VTXO subscription stream ended without error — arkd closed the stream. Restarting scripts view.");
            await UpdateScriptsView(_shutdownCts.Token);
        }
    }

    private async Task FirePollSchedule(HashSet<string> scripts, CancellationToken token)
    {
        for (var i = 0; i < StreamPushPollSchedule.Length; i++)
        {
            try
            {
                await Task.Delay(StreamPushPollSchedule[i], token);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                var after = DateTimeOffset.UtcNow - StreamPollLookback;
                await _readyToPoll.Writer.WriteAsync(new PollRequest(scripts, after), token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "FirePollSchedule: failed to enqueue retry poll ({Attempt}/{Total})",
                    i + 1, StreamPushPollSchedule.Length);
            }
        }
    }

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
                _logger?.LogInformation(
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
                _logger?.LogInformation(
                    "StartQueryLogic: poll returned {Found} VTXO(s) across {Count} script(s) in {Elapsed}ms",
                    found, request.Scripts.Count, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);

                // Advance the persisted full-poll cursor only after a successful
                // poll that was enqueued as a full-set snapshot. Per-script and
                // stream-driven polls never advance it.
                if (request.IsFullSetSnapshot && _syncStateStorage is not null)
                {
                    try
                    {
                        await _syncStateStorage.SetLastFullPollAtAsync(request.StartedAt, cancellationToken);
                    }
                    catch (Exception persistEx)
                    {
                        _logger?.LogWarning(persistEx,
                            "Failed to persist LastFullPollAt={At}; cold-start catch-up will fall back to a longer window",
                            request.StartedAt.ToString("O"));
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

        // Log equivalent REST API URL for manual testing (substitute your arkd host:port).
        var queryParams = string.Join("&", scripts.Select(s => $"scripts={Uri.EscapeDataString(s)}"));
        if (after.HasValue)
            queryParams += $"&after={after.Value.ToUnixTimeMilliseconds()}";
        _logger?.LogInformation("PollScriptsForVtxos: curl http://localhost:7070/v1/indexer/vtxos?{QueryParams}", queryParams);

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

        _logger?.LogInformation("VTXO synchronization service disposed");
    }
}