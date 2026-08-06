namespace NArk.ArkadeIntents.Models;

/// <summary>
/// Lifecycle of a non-interactive swap, derived from the covenant VTXO's on-chain state (mirrors
/// the arkade wallet's <c>AssetSwapStatus</c>).
/// </summary>
public enum ArkadeSwapIntentStatus
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

    /// <summary>
    /// The refund deadline passed with the deposit still unspent (<see cref="ArkadeSwapIntentType.BtcToLightning"/>
    /// only). Anyone may now push the covenant refund, which can pay nowhere but the maker's own address.
    /// </summary>
    Refundable,

    /// <summary>
    /// The covenant VTXO was spent once both the fill and refund paths were live
    /// (<see cref="ArkadeSwapIntentType.BtcToLightning"/> only), so which one it was is decidable
    /// only from the spending witness — a preimage there means the solver filled. Either way the
    /// maker's exposure is over.
    /// </summary>
    Resolved,
}
