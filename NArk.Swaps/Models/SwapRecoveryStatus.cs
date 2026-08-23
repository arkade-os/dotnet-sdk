namespace NArk.Swaps.Models;

/// <summary>
/// Diagnostic conclusion produced by <see cref="SwapRecoveryInfo"/>.
/// </summary>
public enum SwapRecoveryStatus
{
    /// <summary>The swap id wasn't found in local storage.</summary>
    SwapNotFound,
    /// <summary>The swap is still <see cref="ArkSwapStatus.Pending"/>;
    /// recovery isn't applicable.</summary>
    StillPending,
    /// <summary>The swap already settled successfully — no recovery
    /// needed.</summary>
    AlreadySettled,
    /// <summary>The swap was already refunded — no recovery needed.</summary>
    AlreadyRefunded,
    /// <summary>The swap's contract script is empty of unspent VTXOs;
    /// nothing to recover. Either funds were never locked or have
    /// already been swept.</summary>
    NoFunds,
    /// <summary>Funds are present at the swap's contract script and
    /// the swap is in a non-terminal-success state. Recovery will run
    /// automatically on the next routine poll.</summary>
    Recoverable,
    /// <summary>The inspection itself failed (arkd snapshot, etc.) —
    /// see <see cref="SwapRecoveryInfo.Error"/>. Try again later.</summary>
    InspectionError,
}
