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

    /// <summary>Map a covenant VTXO's lifecycle to a terminal swap status, or <c>null</c> while still open.</summary>
    public static ArkadeSwapIntentStatus? ResolveTerminalStatus(ArkVtxo vtxo)
    {
        if (vtxo.IsSpent()) return ArkadeSwapIntentStatus.Fulfilled;
        if (vtxo.Swept) return ArkadeSwapIntentStatus.Recoverable;
        return null;
    }

    /// <summary>
    /// Map a Lightning swap's lockup VTXO to its status, given when the covenant refund opens.
    /// </summary>
    /// <param name="vtxo">The lockup VTXO.</param>
    /// <param name="refundLocktime">Unix seconds at which the refund path opens.</param>
    /// <param name="now">Current time, unix seconds.</param>
    /// <returns>The status to move to, or <c>null</c> while nothing has changed.</returns>
    /// <remarks>
    /// Unlike the asset directions, a spent lockup here is not automatically a fill: this covenant
    /// carries a refund leaf that anyone may spend once its CLTV matures. Before that deadline the
    /// refund is unspendable, so a spend can only be the solver claiming with the preimage and the
    /// swap is <see cref="ArkadeSwapIntentStatus.Fulfilled"/>. At or after it both leaves are live, so
    /// the outcome is reported as <see cref="ArkadeSwapIntentStatus.Resolved"/> rather than guessed —
    /// attributing it needs the spending witness, where a preimage means the solver filled.
    /// </remarks>
    public static ArkadeSwapIntentStatus? ResolveLightningStatus(ArkVtxo vtxo, long refundLocktime, long now)
    {
        if (vtxo.IsSpent())
        {
            return now < refundLocktime
                ? ArkadeSwapIntentStatus.Fulfilled
                : ArkadeSwapIntentStatus.Resolved;
        }
        if (vtxo.Swept) return ArkadeSwapIntentStatus.Recoverable;
        if (now >= refundLocktime) return ArkadeSwapIntentStatus.Refundable;
        return null;
    }

    /// <summary>
    /// Map a Lightning <em>receive</em> swap's lockup VTXO to its status.
    /// </summary>
    /// <param name="vtxo">The lockup VTXO the solver funded.</param>
    /// <param name="refundLocktime">Unix seconds at which the solver's own reclaim path opens.</param>
    /// <param name="now">Current time, unix seconds.</param>
    /// <returns>The status to move to, or <c>null</c> while nothing has changed.</returns>
    /// <remarks>
    /// The roles invert here, and so does what an unspent lockup means. On the send leg it is a swap
    /// still waiting on the solver; here the solver has already paid out, and the covenant holds
    /// money only our preimage can move — so an unspent lockup is a call to act, on a clock that
    /// ends when the solver's reclaim opens. A spend before that deadline can only be our own claim;
    /// at or after it, either party could have moved, so the outcome is reported rather than guessed.
    /// </remarks>
    public static ArkadeSwapIntentStatus? ResolveLightningReceiveStatus(
        ArkVtxo vtxo, long refundLocktime, long now)
    {
        if (vtxo.IsSpent())
        {
            return now < refundLocktime
                ? ArkadeSwapIntentStatus.Fulfilled
                : ArkadeSwapIntentStatus.Resolved;
        }
        if (vtxo.Swept) return ArkadeSwapIntentStatus.Recoverable;
        // Past the deadline an unclaimed lockup is the solver's to take back; there is nothing for
        // us to report until it actually moves.
        return now < refundLocktime ? ArkadeSwapIntentStatus.Claimable : null;
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            var swap = (await _intentStorage.GetArkadeSwapIntents(swapPkScript: vtxo.Script)).FirstOrDefault();

            // A Lightning swap's covenant has a refund leaf, so the same VTXO state means different
            // things either side of its deadline; the asset directions have no such leaf.
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            var status = swap switch
            {
                { Type: ArkadeSwapIntentType.BtcToLightning, RefundLocktime: { } send } =>
                    ResolveLightningStatus(vtxo, send, now),
                { Type: ArkadeSwapIntentType.LightningToBtc, RefundLocktime: { } receive } =>
                    ResolveLightningReceiveStatus(vtxo, receive, now),
                _ => ResolveTerminalStatus(vtxo),
            };

            if (status is null || status == swap?.Status) return;

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
