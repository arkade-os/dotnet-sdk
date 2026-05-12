using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.VTXOs;
using NArk.Core.Models.Options;

namespace NArk.Core.Services;

/// <summary>
/// Background service that fetches and stores the virtual-tx chain for
/// every new VTXO observed via <see cref="IVtxoStorage.VtxosChanged"/>.
///
/// VTXOs arrive from multiple sources — batch settlement, change from a
/// spend, incoming payment from another wallet, swap claim, sweep, etc. —
/// and any of them can later need to be exited unilaterally. By
/// subscribing to the storage-level event we capture them all uniformly,
/// regardless of how they arrived.
///
/// Storage is opt-in — register via
/// <c>services.AddVirtualTxAutoFetch()</c>. With the auto-fetch off,
/// callers can still drive <see cref="VirtualTxService"/> manually
/// (e.g. only when starting an exit) if they prefer to defer the
/// indexer round-trips.
/// </summary>
public class VtxoChainAutoFetchService(
    IVtxoStorage vtxoStorage,
    VirtualTxService virtualTxService,
    IOptions<VirtualTxOptions> options,
    ILogger<VtxoChainAutoFetchService>? logger = null) : IHostedService, IDisposable
{
    private readonly Channel<ArkVtxo> _queue = Channel.CreateUnbounded<ArkVtxo>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _worker;
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged += OnVtxosChanged;
        _worker = ProcessQueueAsync(_shutdownCts.Token);
        logger?.LogInformation("VtxoChainAutoFetchService started (mode={Mode}, minAmount={MinAmount})",
            options.Value.DefaultMode, options.Value.MinExitWorthAmount);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged -= OnVtxosChanged;
        _queue.Writer.TryComplete();
        await _shutdownCts.CancelAsync();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* shutdown */ }
        }
        logger?.LogInformation("VtxoChainAutoFetchService stopped");
    }

    private void OnVtxosChanged(object? sender, ArkVtxo vtxo)
    {
        // We only need exit data for VTXOs we could plausibly want to
        // exit later. Skip if it's already spent (nothing to exit) or
        // below the configured worth-threshold (fees would exceed value).
        if (vtxo.IsSpent()) return;
        if (vtxo.Amount < options.Value.MinExitWorthAmount) return;
        _queue.Writer.TryWrite(vtxo);
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var vtxo in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // FetchAndStoreBranchAsync is idempotent: it short-
                    // circuits on the storage's HasBranchAsync check, so
                    // duplicate events for the same VTXO are cheap.
                    await virtualTxService.FetchAndStoreBranchAsync(
                        vtxo.OutPoint, options.Value.DefaultMode, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex,
                        "Failed to fetch virtual tx chain for VTXO {Outpoint}", vtxo.OutPoint);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdownCts.Dispose();
    }
}
