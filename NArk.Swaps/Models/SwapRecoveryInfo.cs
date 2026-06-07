namespace NArk.Swaps.Models;

/// <summary>
/// Diagnostic snapshot of a swap's recovery state — produced by
/// <c>SwapsManagementService.InspectSwapRecoveryAsync</c> and
/// <c>ScanRecoverableSwapsAsync</c>. Mirrors the
/// <c>SubmarineRecoveryInfo</c> shape from <c>arkade-os/boltz-swap</c>'s
/// TS SDK but covers every <see cref="ArkSwapType"/>, not just submarine.
/// </summary>
/// <remarks>
/// This is a read-only report. Recovery itself happens automatically
/// inside <c>BoltzSwapProvider.PollSwapState</c> on the next routine
/// poll once a swap reaches a refundable Boltz status; callers don't
/// need to invoke anything extra after seeing
/// <see cref="SwapRecoveryStatus.Recoverable"/>. The intended use is
/// surfacing "X sats stranded — recovery will run automatically"
/// indicators in wallet UIs and audit reports.
/// </remarks>
public class SwapRecoveryInfo
{
    /// <summary>The swap id this info refers to.</summary>
    public required string SwapId { get; init; }

    /// <summary>
    /// The full swap record, when found in storage. <c>null</c> only when
    /// <see cref="Status"/> is <see cref="SwapRecoveryStatus.SwapNotFound"/>.
    /// </summary>
    public ArkSwap? Swap { get; init; }

    /// <summary>The diagnostic conclusion for this swap.</summary>
    public required SwapRecoveryStatus Status { get; init; }

    /// <summary>Number of unspent VTXOs found at the swap's contract
    /// script. Zero except when <see cref="Status"/> is
    /// <see cref="SwapRecoveryStatus.Recoverable"/>.</summary>
    public int VtxoCount { get; init; }

    /// <summary>Total sats locked at the swap's contract script
    /// (sum of unspent VTXO amounts).</summary>
    public long AmountSats { get; init; }

    /// <summary>Inspection-side error message, if any (e.g. arkd
    /// snapshot poll failed). Recovery itself is unaffected — the
    /// routine poll loop will retry on the next tick.</summary>
    public string? Error { get; init; }
}
