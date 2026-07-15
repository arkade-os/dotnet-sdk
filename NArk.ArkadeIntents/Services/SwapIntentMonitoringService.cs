using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;

namespace NArk.Arkade.NonInteractiveSwaps;

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
/// ignores non-pending swaps, so a swap moved to <see cref="SwapIntentStatus.Cancelling"/> before its
/// cancel-spend is never read as a fill.
/// </remarks>
public sealed class SwapIntentMonitoringService : IHostedService
{
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly ILogger<SwapIntentMonitoringService>? _logger;

    public SwapIntentMonitoringService(
        IVtxoStorage vtxoStorage,
        IArkadeIntentStorage intentStorage,
        ILogger<SwapIntentMonitoringService>? logger = null)
    {
        _vtxoStorage = vtxoStorage;
        _intentStorage = intentStorage;
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

    /// <summary>Map a covenant VTXO's lifecycle to a terminal swap status, or <c>null</c> while still open.</summary>
    public static SwapIntentStatus? ResolveTerminalStatus(ArkVtxo vtxo)
    {
        if (vtxo.IsSpent()) return SwapIntentStatus.Fulfilled;
        if (vtxo.Swept) return SwapIntentStatus.Recoverable;
        return null;
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            var status = ResolveTerminalStatus(vtxo);
            if (status is null) return;

            var spentTxid = status == SwapIntentStatus.Fulfilled
                ? vtxo.ArkTxid ?? vtxo.SpentByTransactionId
                : null;

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
