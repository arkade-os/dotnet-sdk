using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;

namespace NArk.Core.Services;

/// <summary>
/// Periodically polls boarding UTXOs for confirmation and spend changes.
/// Only polls when unspent unrolled VTXOs exist. Complements event-driven sync
/// to catch missed events (e.g., provider reconnects, block confirmation updates).
/// </summary>
public class BoardingUtxoPollService(
    BoardingUtxoSyncService boardingUtxoSyncService,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
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
                if (!await HasUnspentBoardingVtxosAsync(cancellationToken))
                    continue;

                logger?.LogDebug("Polling boarding UTXOs for confirmation/spend changes");
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

    private async Task<bool> HasUnspentBoardingVtxosAsync(CancellationToken cancellationToken)
    {
        var boardingContracts = await contractStorage.GetContracts(
            scope: ContractScope.Onchain,
            cancellationToken: cancellationToken);

        if (boardingContracts.Count == 0)
            return false;

        var scripts = boardingContracts.Select(c => c.Script).ToArray();
        var unspentVtxos = await vtxoStorage.GetVtxos(
            scripts: scripts,
            includeSpent: false,
            cancellationToken: cancellationToken);

        return unspentVtxos.Any(v => v.Unrolled);
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
