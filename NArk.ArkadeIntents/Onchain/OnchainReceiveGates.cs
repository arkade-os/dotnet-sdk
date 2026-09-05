using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;

namespace NArk.ArkadeIntents.Onchain;

/// <summary>Why an onchain receive quote cannot be funded.</summary>
public enum OnchainReceiveRefusalReason
{
    /// <summary>The quote or its window has already lapsed.</summary>
    Expired,

    /// <summary>Too little time before the solver's Arkade reclaim opens, which is our claim deadline.</summary>
    InsufficientHeadroom,

    /// <summary>The quote is missing a field the corridor cannot proceed without.</summary>
    IncompleteQuote,

    /// <summary>The solver asks for a confirmation count outside what this corridor will wait on.</summary>
    ConfirmationsOutOfRange,

    /// <summary>The L1 locktime leaves no safe window to confirm the funding and settle against it.</summary>
    ClaimWindowTooShort,

    /// <summary>The two refunds open in the wrong order, or too close together.</summary>
    TimelocksOutOfOrder,
}

/// <summary>Thrown when an onchain receive quote is refused before anything is funded.</summary>
public sealed class OnchainReceiveNotFundableException(
    OnchainReceiveRefusalReason reason, string message) : Exception(message)
{
    /// <summary>Which check refused. Branch on this, never on the message.</summary>
    public OnchainReceiveRefusalReason Reason { get; } = reason;
}

/// <summary>
/// The checks run immediately before an on-board funds its L1 HTLC — never at quote time.
/// </summary>
/// <remarks>
/// <para>
/// The same shape of danger as <see cref="OnchainSendGates"/> and the opposite arrangement of it.
/// There the client funded Arkade and its own refund had to open <em>last</em>; here the client
/// funds L1 and the solver's Arkade refund must open <em>first</em>, because the solver is the one
/// paying out ahead of being paid.
/// </para>
/// <para>
/// The numbers are bound to the send leg's rather than copied, because they measure the same
/// physical facts — a block interval, and the time it takes to get a spend confirmed. Two constants
/// that must agree and are written twice are two constants that will eventually disagree, and the
/// symptom is a corridor that funds swaps its own mirror image refuses.
/// </para>
/// </remarks>
public static class OnchainReceiveGates
{
    /// <summary>The most confirmations this corridor will wait for. <see cref="OnchainSendGates.MaxMinConfirmations"/>.</summary>
    public const int MaxMinConfirmations = OnchainSendGates.MaxMinConfirmations;

    /// <summary>Nominal seconds per block. <see cref="OnchainSendGates.SecondsPerBlock"/>.</summary>
    public const long SecondsPerBlock = OnchainSendGates.SecondsPerBlock;

    /// <summary>
    /// Time that must remain on the L1 leg after the funding has confirmed.
    /// </summary>
    /// <remarks>
    /// On this leg it is the <em>solver's</em> claim that has to fit inside it. That is still our
    /// problem: a solver that cannot safely claim will not fund the Arkade side at all, so a quote
    /// leaving it no room is one whose L1 funding would sit there until we refunded it.
    /// </remarks>
    public const long ClaimMarginSeconds = OnchainSendGates.ClaimMarginSeconds;

    /// <summary>Minimum time before our Arkade claim window closes. <see cref="OnchainSendGates.MinHeadroomSeconds"/>.</summary>
    public const long MinHeadroomSeconds = OnchainSendGates.MinHeadroomSeconds;

    /// <summary>
    /// How far the solver's Arkade refund must open <em>before</em> our L1 one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference solver sizes its own Arkade refund as <c>htlc_locktime</c> minus exactly this,
    /// capped, so a well-formed quote satisfies this by construction and nothing legitimate is
    /// refused by checking it. What it catches is a quote that is not well formed: the ordering is
    /// the corridor's central safety property and neither contract enforces it, so a client that
    /// does not check it is trusting the counterparty for the one thing the design says not to.
    /// </para>
    /// <para>
    /// Which way round matters. The solver claims L1 with the preimage our Arkade claim published,
    /// so its Arkade lockup must become reclaimable before our L1 refund does — otherwise there is a
    /// window in which we can take the L1 sats back while still holding a claimable Arkade lockup,
    /// and one leg pays for both. Refusing here is refusing to be handed that window: a swap we
    /// could only complete by robbing the counterparty is one no honest solver offered.
    /// </para>
    /// </remarks>
    public const long OrderMarginSeconds = 15 * 60;

    /// <summary>
    /// Whether there is still time to claim the Arkade lockup before the solver's reclaim opens.
    /// </summary>
    /// <param name="arkadeRefundLocktime">The quote's <c>refund_locktime</c>, unix seconds.</param>
    /// <param name="now">Unix seconds.</param>
    /// <returns><c>true</c> while claiming is still safe.</returns>
    /// <remarks>
    /// The mirror of <see cref="OnchainSendGates.ClaimWindowIsOpen"/>, on the rail the roles put it.
    /// Claiming into a closing window is a race we can lose after showing our hand: the claim
    /// publishes the preimage, so losing it hands the solver both legs.
    /// </remarks>
    public static bool ClaimWindowIsOpen(long arkadeRefundLocktime, long now) =>
        arkadeRefundLocktime - now >= ClaimMarginSeconds;

    /// <summary>
    /// Whether the L1 HTLC's refund leaf has matured.
    /// </summary>
    /// <param name="htlcLocktime">The leaf's absolute locktime, unix seconds.</param>
    /// <param name="medianTimePast">
    /// The chain tip's median time past (BIP-113) — <em>not</em> wall clock.
    /// </param>
    /// <returns><c>true</c> once a refund spend would be accepted.</returns>
    /// <remarks>
    /// Consensus matures CLTV against median time past, which trails wall clock by around an hour.
    /// Comparing against a local clock therefore produces a transaction that looks due and is
    /// rejected as non-final, and the rejection arrives with no indication that the deadline was
    /// simply read off the wrong clock.
    /// </remarks>
    public static bool RefundIsDue(long htlcLocktime, long medianTimePast) =>
        medianTimePast >= htlcLocktime;

    /// <summary>
    /// Refuse a quote this corridor cannot safely fund.
    /// </summary>
    /// <param name="quote">The solver's quote.</param>
    /// <param name="now">The current time, unix seconds.</param>
    /// <exception cref="OnchainReceiveNotFundableException">Any check refused.</exception>
    public static void AssertFundable(RfqQuote<OnchainReceiveQuoteProfile> quote, long now)
    {
        if (now >= quote.ValidUntil)
        {
            throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.Expired, "the quote has expired — request a fresh one");
        }

        // The solver's Arkade refund is OUR claim deadline on this leg, which is why the send leg's
        // headroom rule applies to it here rather than to a deadline of our own.
        if (quote.RefundLocktime - now < MinHeadroomSeconds)
        {
            throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.InsufficientHeadroom,
                $"only {quote.RefundLocktime - now}s remain before the solver's Arkade reclaim opens, "
                + $"need {MinHeadroomSeconds}s to take delivery in");
        }

        if (quote.Profile?.HtlcLocktime is not { } htlcLocktime)
        {
            throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.IncompleteQuote,
                "the quote carries no htlc_locktime, so the L1 leg's deadline is unknown");
        }

        if (quote.Profile.MinConfirmations is not { } minConfirmations)
        {
            throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.IncompleteQuote,
                "the quote carries no min_confirmations, so there is no telling when the solver will "
                + "act on our funding");
        }

        if (minConfirmations < 1 || minConfirmations > MaxMinConfirmations)
        {
            throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.ConfirmationsOutOfRange,
                $"the solver asks for {minConfirmations} confirmations, outside the 1..{MaxMinConfirmations} "
                + "this corridor will wait for");
        }

        // Room for our funding to confirm AND for the solver to settle against it well before the
        // L1 refund opens. Too little, and it declines to fund Arkade at all — leaving our sats
        // parked in an HTLC until we take them back.
        var needed = minConfirmations * SecondsPerBlock + ClaimMarginSeconds;
        if (htlcLocktime - now <= needed)
        {
            throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.ClaimWindowTooShort,
                $"the L1 locktime leaves {htlcLocktime - now}s to confirm and settle in, need more than {needed}s");
        }

        // The solver's Arkade refund opens FIRST — the inverse of the send leg, and for the inverse
        // reason: here it is the solver that pays out ahead of being paid.
        if (quote.RefundLocktime + OrderMarginSeconds > htlcLocktime)
        {
            throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.TimelocksOutOfOrder,
                $"the solver's Arkade refund opens at {quote.RefundLocktime} and our L1 refund at "
                + $"{htlcLocktime}, leaving under the {OrderMarginSeconds}s margin the ordering needs");
        }
    }
}
