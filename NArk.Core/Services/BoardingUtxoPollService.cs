using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NArk.Core.Services;

/// <summary>
/// Periodically polls the Bitcoin blockchain for boarding UTXOs.
/// Delegates to <see cref="BoardingUtxoSyncService"/> which exits early when no
/// boarding contracts are registered.
/// </summary>
public class BoardingUtxoPollService(
    BoardingUtxoSyncService boardingUtxoSyncService,
    ILogger<BoardingUtxoPollService>? logger = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = PollLoopAsync(_cts.Token);
        logger?.LogInformation("BoardingUtxoPollService started (interval={Interval}s)", PollInterval.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_pollTask is not null)
        {
            try { await _pollTask; }
            catch (OperationCanceledException) { }
        }

        logger?.LogInformation("BoardingUtxoPollService stopped");
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(InitialDelay, cancellationToken);

        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await boardingUtxoSyncService.SyncAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to poll boarding UTXOs");
            }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
