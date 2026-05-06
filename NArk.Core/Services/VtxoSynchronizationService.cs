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
                // entire set. Without a persisted cursor we'd re-fetch every
                // script's full VTXO history every cold start. Use
                // MIN(per-wallet vtxo.lastFullPollAt) as the `after` filter
                // on this one call so the cold-start catch-up window equals
                // "since last shutdown" rather than "all of history".
                DateTimeOffset? catchupAfter = null;
                var isInitialCatchup = _isFirstStartupCatchup;
                if (isInitialCatchup && _walletStorage is not null)
                {
                    try
                    {
                        catchupAfter = await ReadCursorMinAcrossWalletsAsync(token);
                        if (catchupAfter is not null)
                        {
                            _logger?.LogInformation(
                                "First-startup catch-up: using MIN(per-wallet {Key})={After} as `after` filter for {Count} script(s)",
                                LastFullPollAtMetadataKey, catchupAfter.Value.ToString("O"), newlyAdded.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex,
                            "First-startup catch-up: failed to read per-wallet {Key}; falling back to full-history fetch",
                            LastFullPollAtMetadataKey);
                    }
                }
                _isFirstStartupCatchup = false;

                // The cold-start catch-up IS a full-set snapshot: at this moment
                // newlyAdded equals the entire active script view (cold start →
                // _lastViewOfScripts was empty before line 291), and `catchupAfter`
                // is the stored cursor (or null for first-ever startup). On its
                // successful completion we both flip the gate flag and advance
                // the cursor to its StartedAt — eliminating the 5-second window
                // between catch-up success and the first routine poll's cursor
                // write. Subsequent UpdateScriptsView calls (script set grows
                // mid-flight) only carry newly-added scripts and stay
                // IsFullSetSnapshot=false.
                var startedAt = isInitialCatchup ? DateTimeOffset.UtcNow : default;
                await _readyToPoll.Writer.WriteAsync(
                    new PollRequest(
                        newlyAdded,
                        catchupAfter,
                        IsFullSetSnapshot: isInitialCatchup,
                        StartedAt: startedAt,
                        IsColdStartCatchup: isInitialCatchup),
                    token);
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