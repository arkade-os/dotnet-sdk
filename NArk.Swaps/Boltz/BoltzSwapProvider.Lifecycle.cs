using Microsoft.Extensions.Logging;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;

namespace NArk.Swaps.Boltz;

public partial class BoltzSwapProvider
{
     public async Task StartAsync(CancellationToken ct)
    {
        _logger?.LogInformation("Starting Boltz swap provider");
        _linkedStartCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        var multiToken = _linkedStartCts;

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

        // Watch for batch-session completions on refund-without-receiver intents so we can
        // trigger a poll immediately rather than waiting up to 1 minute for RoutinePoll.
        _intentStorage.IntentChanged += OnRefundIntentChanged;
    }

    private void OnRefundIntentChanged(object? sender, ArkIntent intent)
    {
        if (!_intentToSwapId.TryGetValue(intent.IntentTxId, out var swapId))
            return;

        if (intent.State is ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled)
        {
            _logger?.LogInformation(
                "Refund intent {IntentTxId} for swap {SwapId} reached terminal state {State} — triggering poll",
                intent.IntentTxId, swapId, intent.State);
            _triggerChannel.Writer.TryWrite($"id:{swapId}");
        }
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

        _intentStorage.IntentChanged -= OnRefundIntentChanged;
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

        // Dispose the linked CTS allocated in StartAsync so we don't leak the
        // CancellationTokenRegistration that ties it back to the caller's token.
        _linkedStartCts?.Dispose();
        _linkedStartCts = null;
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