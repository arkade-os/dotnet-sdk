namespace NArk.Arkade.NonInteractiveSwaps;

/// <summary>
/// Lifecycle of a non-interactive swap, derived from the covenant VTXO's on-chain state (mirrors
/// the arkade wallet's <c>AssetSwapStatus</c>).
/// </summary>
public enum SwapIntentStatus
{
    /// <summary>Deposit funded; waiting for the solver to fill (or for expiry).</summary>
    Pending,

    /// <summary>The cancel path is being spent; set before spending so the monitor can't read the cancel as a fill.</summary>
    Cancelling,

    /// <summary>The solver spent the covenant VTXO — the swap completed.</summary>
    Fulfilled,

    /// <summary>The swap was cancelled and the deposit returned.</summary>
    Cancelled,

    /// <summary>The covenant VTXO expired/was swept without a fill; the deposit is recoverable on-chain.</summary>
    Recoverable,
}
