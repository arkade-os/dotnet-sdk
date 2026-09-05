using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;

namespace NArk.ArkadeIntents.Onchain;

/// <summary>Why an onchain send quote cannot be funded.</summary>
public enum OnchainSendRefusalReason
{
    /// <summary>The quote or its window has already lapsed.</summary>
    Expired,

    /// <summary>Too little time before the Arkade refund path opens.</summary>
    InsufficientHeadroom,

    /// <summary>The quote is missing a field the corridor cannot proceed without.</summary>
    IncompleteQuote,

    /// <summary>The solver asks for a confirmation count outside what this corridor will wait for.</summary>
    ConfirmationsOutOfRange,

    /// <summary>The L1 locktime leaves no safe window to confirm the funding and claim it.</summary>
    ClaimWindowTooShort,

    /// <summary>The two refunds open in the wrong order, or too close together.</summary>
    TimelocksOutOfOrder,
}

/// <summary>Thrown when an onchain send quote is refused before anything is funded.</summary>
public sealed class OnchainSendNotFundableException(
    OnchainSendRefusalReason reason, string message) : Exception(message)
{
    /// <summary>Which check refused. Branch on this, never on the message.</summary>
    public OnchainSendRefusalReason Reason { get; } = reason;
}

/// <summary>
/// The checks run immediately before an onchain send is funded — never at quote time.
/// </summary>
/// <remarks>
/// This corridor's danger is not in either contract but in the relationship between them. Two
/// deadlines on two chains govern one swap, and getting their order wrong is the single failure that
/// can cost both legs at once, which is why it is checked here rather than left to the solver.
/// </remarks>
public static class OnchainSendGates
{
    /// <summary>The most confirmations this corridor will wait for before claiming.</summary>
    /// <remarks>
    /// A ceiling, not a preference: a solver naming a large number turns the claim window into a
    /// wait the locktime may not outlast, and the quote is refusable now rather than stuck later.
    /// </remarks>
    public const int MaxMinConfirmations = 6;

    /// <summary>Nominal seconds per block, for turning a confirmation count into a wait.</summary>
    public const long SecondsPerBlock = 600;

    /// <summary>Time that must remain to claim on L1 after the funding has confirmed.</summary>
    public const long ClaimMarginSeconds = 90 * 60;

    /// <summary>
    /// How far the Arkade refund must open <em>after</em> the L1 one.
    /// </summary>
    /// <remarks>
    /// Reorg margin. The solver takes the Arkade side using the preimage the client's L1 claim
    /// revealed, so the client's own Arkade refund must be the last door to open — otherwise there
    /// is a window in which the client can reclaim on Arkade while the solver can still reclaim on
    /// L1, and one leg pays for both.
    /// </remarks>
    public const long OrderMarginSeconds = 2 * 60 * 60;

    /// <summary>Minimum time before the Arkade refund opens, mirroring the Lightning send leg.</summary>
    public const long MinHeadroomSeconds = 90 * 60;

    /// <summary>
    /// Whether there is still time to claim the L1 HTLC before its refund leaf opens.
    /// </summary>
    /// <param name="htlcLocktime">When the counterparty's refund leaf matures, unix seconds.</param>
    /// <param name="now">Unix seconds.</param>
    /// <returns><c>true</c> while claiming is still safe.</returns>
    /// <remarks>
    /// Claiming near the locktime is a race that can be lost after showing our hand: a broadcast
    /// that does not confirm before the counterparty's refund does leaves it with its sats back and
    /// our preimage out of the mempool, which takes the Arkade side too. Both legs, for the sake of
    /// a few minutes. Declining costs only the covenant refund, which is still ours.
    /// </remarks>
    public static bool ClaimWindowIsOpen(long htlcLocktime, long now) =>
        htlcLocktime - now >= ClaimMarginSeconds;

    /// <summary>
    /// Refuse a quote this corridor cannot safely fund.
    /// </summary>
    /// <param name="quote">The solver's quote.</param>
    /// <param name="now">The current time, unix seconds.</param>
    /// <exception cref="OnchainSendNotFundableException">Any check refused.</exception>
    public static void AssertFundable(RfqQuote<OnchainSendQuoteProfile> quote, long now)
    {
        if (now >= quote.ValidUntil)
        {
            throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.Expired, "the quote has expired — request a fresh one");
        }

        if (quote.RefundLocktime - now < MinHeadroomSeconds)
        {
            throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.InsufficientHeadroom,
                $"only {quote.RefundLocktime - now}s remain before the Arkade refund path opens, "
                + $"need {MinHeadroomSeconds}s");
        }

        if (quote.Profile?.HtlcLocktime is not { } htlcLocktime)
        {
            throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.IncompleteQuote,
                "the quote carries no htlc_locktime, so the L1 leg's deadline is unknown");
        }

        if (quote.Profile.MinConfirmations is not { } minConfirmations)
        {
            throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.IncompleteQuote,
                "the quote carries no min_confirmations, so there is no telling when the L1 funding "
                + "is safe to act on");
        }

        if (minConfirmations < 1 || minConfirmations > MaxMinConfirmations)
        {
            throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.ConfirmationsOutOfRange,
                $"the solver asks for {minConfirmations} confirmations, outside the 1..{MaxMinConfirmations} "
                + "this corridor will wait for");
        }

        // Enough room to confirm the solver's funding AND claim it well before the L1 refund opens.
        var needed = minConfirmations * SecondsPerBlock + ClaimMarginSeconds;
        if (htlcLocktime - now <= needed)
        {
            throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.ClaimWindowTooShort,
                $"the L1 locktime leaves {htlcLocktime - now}s to confirm and claim in, need more than {needed}s");
        }

        // The client's Arkade refund opens LAST. Reversed, the client could reclaim on Arkade while
        // the solver could still reclaim on L1, and the swap pays one party twice.
        if (htlcLocktime + OrderMarginSeconds > quote.RefundLocktime)
        {
            throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.TimelocksOutOfOrder,
                $"the L1 refund opens at {htlcLocktime} and the Arkade refund at {quote.RefundLocktime}, "
                + $"leaving under the {OrderMarginSeconds}s margin the ordering needs");
        }
    }
}
