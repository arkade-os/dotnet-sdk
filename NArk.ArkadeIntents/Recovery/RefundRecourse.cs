using NBitcoin;

namespace NArk.ArkadeIntents.Recovery;

/// <summary>Why a refund cannot be pushed from this wallet, as opposed to not being due yet.</summary>
/// <remarks>
/// The distinction matters because the two look identical to a caller and mean opposite things. "Not
/// yet" resolves itself by waiting; these do not resolve at all until something about the wallet
/// changes, and a loop that treats them as "not yet" retries forever while a window closes.
/// </remarks>
public enum RefundBlockedReason
{
    /// <summary>The wallet holds no signer, so nothing here can sign the refund leaf.</summary>
    NoSigner,

    /// <summary>
    /// The lockup contract is not in this store, so the funded script cannot be rebuilt.
    /// </summary>
    /// <remarks>
    /// Usually a wallet restored from a seed without the contract rows that went with it. The money
    /// is untouched and the swap is not lost — it needs the store that recorded how the script was
    /// built, or a restore that rebuilds it.
    /// </remarks>
    ContractMissing,

    /// <summary>
    /// The contract rebuilds to a script other than the one that was funded.
    /// </summary>
    /// <remarks>
    /// The parameters and the script are stored independently, so a parameter written wrong — or
    /// dropped by a field-mapped backend — yields a contract that looks entirely valid and simply
    /// cannot sign for the money. An Arkade server key rotated since funding does the same.
    /// </remarks>
    ContractMismatch,

    /// <summary>The swap carries no refund locktime, so there is no deadline to test against.</summary>
    NoLocktime,
}

/// <summary>Thrown when a refund is not this wallet's to push, whatever the clock says.</summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> deliberately. The advance loop catches that
/// type to keep one swap's failure from ending a pass over many, so a recovery exception outside
/// that hierarchy would escape the sweep entirely — turning a swap this wallet merely cannot act on
/// into a crash that stops every other swap from being acted on either.
/// </remarks>
public sealed class RefundNotLocallyPossibleException(RefundBlockedReason reason, string message)
    : InvalidOperationException(message)
{
    /// <summary>Which obstacle. Branch on this, never on the message.</summary>
    public RefundBlockedReason Reason { get; } = reason;
}

/// <summary>
/// Thrown when part of a lockup is out of the covenant's reach, so a refund would move only some of
/// the money.
/// </summary>
/// <remarks>
/// <para>
/// The whole push is refused rather than narrowed to what is spendable, and that is the point.
/// Filtering the unreachable outputs out silently is worse than failing: it reports success over
/// money that never moved, and a caller that believes the swap is refunded stops watching the part
/// that is still sitting there.
/// </para>
/// <para>
/// Both causes are recoverable and neither is recoverable <em>here</em>. A swept output goes through
/// the wallet's own recovery path; an exited one needs its unroll finished and then an on-chain
/// spend of the same leaves. Either way the outpoints are named, because a caller has to act on them
/// one at a time.
/// </para>
/// </remarks>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> for the reason
/// <see cref="RefundNotLocallyPossibleException"/> does: the advance loop catches that type, and a
/// partial lockup is a condition to report, never one to take a sweep down with.
/// </remarks>
public sealed class LockupNeedsRecoveryException : InvalidOperationException
{
    /// <summary>The outputs that must be dealt with before a refund can move everything.</summary>
    public IReadOnlyList<OutPoint> Outpoints { get; }

    /// <summary>Why they are out of reach.</summary>
    public LockupFate Fate { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="fate">Whether they were swept or exited.</param>
    /// <param name="outpoints">The outputs needing recovery.</param>
    /// <param name="message">The explanation.</param>
    public LockupNeedsRecoveryException(
        LockupFate fate, IReadOnlyList<OutPoint> outpoints, string message) : base(message)
    {
        Fate = fate;
        Outpoints = outpoints;
    }
}

/// <summary>How an attempt to resolve an unfinished swap ended.</summary>
public enum RefundOutcomeKind
{
    /// <summary>The counterparty resolved it — claimed or returned. Nothing was pushed.</summary>
    Resolved,

    /// <summary>The lockup is still live and the deadline has not passed. Nothing was pushed.</summary>
    NotDue,

    /// <summary>We pushed the refund.</summary>
    Refunded,

    /// <summary>
    /// Part of the lockup must be recovered first — see <see cref="LockupNeedsRecoveryException"/>.
    /// </summary>
    NeedsRecovery,

    /// <summary>The refund is not this wallet's to push — see <see cref="RefundBlockedReason"/>.</summary>
    Blocked,

    /// <summary>The chain said nothing usable. Not an answer; ask again later.</summary>
    Unknown,
}

/// <summary>What an attempt to resolve an unfinished swap found.</summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Fate">What the chain said about the lockup.</param>
/// <param name="Txid">The refund transaction, when one was pushed.</param>
/// <param name="Blocked">Why it is not ours to push, on <see cref="RefundOutcomeKind.Blocked"/>.</param>
/// <param name="Stuck">The outputs needing recovery, on <see cref="RefundOutcomeKind.NeedsRecovery"/>.</param>
/// <param name="Detail">A human-readable elaboration. Never branch on it.</param>
public sealed record RefundOutcome(
    RefundOutcomeKind Kind,
    LockupFate Fate,
    string? Txid = null,
    RefundBlockedReason? Blocked = null,
    IReadOnlyList<OutPoint>? Stuck = null,
    string? Detail = null);
