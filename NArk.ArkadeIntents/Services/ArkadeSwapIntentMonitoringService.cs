using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Models;

namespace NArk.ArkadeIntents.Services;

/// <summary>
/// Reactive glue that turns covenant-VTXO changes into swap-status transitions. It opens no
/// subscription of its own: the pending swaps' covenant scripts reach the shared
/// <c>VtxoSynchronizationService</c> via <see cref="IArkadeIntentStorage"/> (which is the
/// <see cref="NArk.Abstractions.Scripts.IActiveScriptsProvider"/>), and this service simply reacts to
/// <see cref="IVtxoStorage.VtxosChanged"/> and writes the new status back to storage. All change
/// notification lives on the storage (<see cref="IArkadeIntentStorage.SwapsChanged"/>).
/// </summary>
/// <remarks>
/// A spent covenant VTXO means the solver fulfilled the swap; a swept one means it expired and the
/// deposit is recoverable. Only pending swaps transition — <see cref="IArkadeIntentStorage.UpdateStatus"/>
/// ignores non-pending swaps, so a swap moved to <see cref="ArkadeSwapIntentStatus.Cancelling"/> before its
/// cancel-spend is never read as a fill.
/// </remarks>
public sealed class ArkadeSwapIntentMonitoringService : IHostedService
{
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly TimeProvider _time;
    private readonly ILogger<ArkadeSwapIntentMonitoringService>? _logger;

    public ArkadeSwapIntentMonitoringService(
        IVtxoStorage vtxoStorage,
        IArkadeIntentStorage intentStorage,
        TimeProvider? time = null,
        ILogger<ArkadeSwapIntentMonitoringService>? logger = null)
    {
        _vtxoStorage = vtxoStorage;
        _intentStorage = intentStorage;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _vtxoStorage.VtxosChanged += OnVtxoChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _vtxoStorage.VtxosChanged -= OnVtxoChanged;
        return Task.CompletedTask;
    }
    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            var swap = (await _intentStorage.GetArkadeSwapIntents(swapPkScript: vtxo.Script)).FirstOrDefault();

            // A Lightning swap's covenant has a refund leaf, so the same VTXO state means different
            // things either side of its deadline; the asset directions have no such leaf.
            if (swap is null) return;

            // One machine decides every transition, guarded by the state the swap is already in —
            // which is what tells our own cancel-spend apart from a counterparty's fill.
            var status = ArkadeSwapStateMachine.Next(
                swap.Type,
                swap.Status,
                SwapObservation.From(vtxo, _time.GetUtcNow().ToUnixTimeSeconds(), swap.RefundLocktime));

            if (status is null || status == swap.Status) return;

            var spentTxid = vtxo.IsSpent() ? vtxo.ArkTxid ?? vtxo.SpentByTransactionId : null;

            // Only pending swaps on this script transition — the storage enforces the race guard.
            if (await _intentStorage.UpdateStatus(vtxo.Script, status.Value, spentTxid))
            {
                _logger?.LogInformation("Swap covenant {Script} → {Status}", vtxo.Script, status.Value);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to update swap status for script {Script}", vtxo.Script);
        }
    }
}
