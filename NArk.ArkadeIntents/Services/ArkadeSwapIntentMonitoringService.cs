using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.Core.Transport;

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
/// deposit is recoverable. Only in-flight swaps transition — <see cref="IArkadeIntentStorage.UpdateStatus"/>
/// ignores terminal and <see cref="ArkadeSwapIntentStatus.Cancelling"/> swaps, so a swap moved to
/// Cancelling before its cancel-spend is never read as a fill.
/// </remarks>
public sealed class ArkadeSwapIntentMonitoringService : IHostedService
{
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly IClientTransport _transport;
    private readonly TimeProvider _time;
    private readonly ILogger<ArkadeSwapIntentMonitoringService>? _logger;

    /// <summary>Creates the monitor.</summary>
    /// <param name="vtxoStorage">The chain view the covenant VTXOs change in.</param>
    /// <param name="intentStorage">The swaps to transition, and the race guard on writing them.</param>
    /// <param name="transport">Where the spending transaction is fetched from, to prove a fill.</param>
    /// <param name="time">Clock for the timelock comparisons; defaults to the system clock.</param>
    /// <param name="logger">Optional logger.</param>
    public ArkadeSwapIntentMonitoringService(
        IVtxoStorage vtxoStorage,
        IArkadeIntentStorage intentStorage,
        IClientTransport transport,
        TimeProvider? time = null,
        ILogger<ArkadeSwapIntentMonitoringService>? logger = null)
    {
        _vtxoStorage = vtxoStorage;
        _intentStorage = intentStorage;
        _transport = transport;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _vtxoStorage.VtxosChanged += OnVtxoChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
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
            if (swap is null || ComposedRouteExecutionGuard.IsCompositionOwned(swap)) return;

            // A spent HTLC lockup moves only on a verdict; one not yet provable either way is left
            // for the advance pass to re-read, rather than written off as Resolved.
            var htlcSpend = vtxo.IsSpent() && SwapSpendVerdict.IsHtlcCorridor(swap.Type);
            var preimageRevealed = false;
            if (htlcSpend)
            {
                if (await SwapSpendVerdict.ClaimedAsync(_transport, _vtxoStorage, swap, CancellationToken.None)
                    is not { } claimed) return;
                preimageRevealed = claimed;
            }

            var status = ArkadeSwapStateMachine.Next(
                swap.Type,
                swap.Status,
                SwapObservation.From(
                    vtxo, _time.GetUtcNow().ToUnixTimeSeconds(), swap.RefundLocktime, preimageRevealed));
            if (status is { } next && htlcSpend && !preimageRevealed)
                status = SwapSpendVerdict.AfterNoClaim(swap.Type, next);

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

    /// <summary>
    /// Whether the spend of this swap's lockup revealed the preimage that settles it.
    /// </summary>
    /// <remarks>
    /// Only a Lightning corridor carries a hash to check against, and only a spent lockup has a
    /// spend to check. A fetch failure reads as "no proof" rather than throwing — an unreachable
    /// indexer must not stop the transition, and a swap misread this way is corrected by the next
    /// <see cref="ArkadeIntentsService.ReconcileAsync"/>.
    /// </remarks>
}
