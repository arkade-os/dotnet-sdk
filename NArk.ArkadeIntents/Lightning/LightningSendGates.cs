using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>Why a maker refused to fund a quote it had already received.</summary>
public enum FundingRefusal
{
    /// <summary>The invoice is expired.</summary>
    InvoiceExpired,

    /// <summary>The quote's <c>valid_until</c> has passed — ask for a fresh one.</summary>
    QuoteExpired,

    /// <summary>Too little time remains before the refund path opens.</summary>
    InsufficientHeadroom,

    /// <summary>The quote does not pay the invoice's amount, or asks for more than it pays out.</summary>
    AmountMismatch,
}

/// <summary>Thrown when a maker's own gates refuse to fund a quote.</summary>
public sealed class LightningSendNotFundableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Which gate refused.</param>
    /// <param name="message">A human-readable elaboration.</param>
    public LightningSendNotFundableException(FundingRefusal reason, string message) : base(message)
        => Reason = reason;

    /// <summary>Which gate refused. Branch on this, never on the message.</summary>
    public FundingRefusal Reason { get; }
}

/// <summary>Thrown when the solver's address matches neither shape the maker derives.</summary>
/// <remarks>
/// Never fund past this. It is the single check that makes a wrong or malicious solver able to
/// produce only an address the maker declines, rather than one that traps funds. Two shapes are
/// compared, not one, because the covenant suite's timelocked refund leaf postdates some
/// deployments and nothing on the wire says whether a given solver funds with it — reaching this
/// exception means the quoted address matched NEITHER derivation, not merely a single guessed one.
/// </remarks>
public sealed class LockupAddressMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="derivedEightLeaf">The address derived without the timelocked refund leaf.</param>
    /// <param name="derivedNineLeaf">The address derived with it.</param>
    /// <param name="quoted">The address the solver sent for comparison.</param>
    public LockupAddressMismatchException(string derivedEightLeaf, string derivedNineLeaf, string? quoted)
        // Every candidate belongs in the message. A mismatch is a derivation disagreement, and the
        // first thing anyone needs is the addresses side by side — a bare "they differ" turns a
        // one-glance diff into a debugging session, at exactly the moment funding is on the line.
        : base("solver's lockup address matches neither shape we derive — refusing to fund. " +
               $"eight-leaf {derivedEightLeaf}, nine-leaf {derivedNineLeaf}, quoted {quoted ?? "<none>"}")
    {
        DerivedEightLeaf = derivedEightLeaf;
        DerivedNineLeaf = derivedNineLeaf;
        Quoted = quoted;
    }

    /// <summary>What the maker derived without the timelocked refund leaf.</summary>
    public string DerivedEightLeaf { get; }

    /// <summary>What the maker derived with the timelocked refund leaf.</summary>
    public string DerivedNineLeaf { get; }

    /// <summary>What the solver claimed, if anything.</summary>
    public string? Quoted { get; }
}

/// <summary>
/// The maker's safety gates: pure decisions over a quote, an invoice and a clock.
/// </summary>
/// <remarks>
/// Kept free of I/O so each gate is testable at its exact boundary, and evaluated <b>immediately
/// before funding</b> rather than at quote time — quoting and funding are separated by network
/// waits, and a check that passed when the quote arrived can be false by the time money moves.
/// </remarks>
public static class LightningSendGates
{
    /// <summary>
    /// The minimum time that must remain before the refund path opens, in seconds.
    /// </summary>
    /// <remarks>
    /// The refund deadline matures against median-time-past, which lags wall clock by roughly an
    /// hour, so a smaller margin is no margin at all.
    /// </remarks>
    public const long MinHeadroomSeconds = 90 * 60;

    /// <summary>
    /// Refuse to fund unless every precondition still holds.
    /// </summary>
    /// <param name="quote">The solver's quote.</param>
    /// <param name="invoiceAmountSats">The invoice's amount, which the solver must pay in full.</param>
    /// <param name="invoiceExpiresAt">The invoice's absolute expiry, unix seconds.</param>
    /// <param name="now">The current time, unix seconds.</param>
    /// <exception cref="LightningSendNotFundableException">A gate refused.</exception>
    public static void AssertFundable(RfqQuote<LightningSendQuoteProfile> quote, long invoiceAmountSats, long invoiceExpiresAt, long now)
    {
        if (now >= invoiceExpiresAt)
        {
            throw new LightningSendNotFundableException(
                FundingRefusal.InvoiceExpired, "the invoice has expired");
        }

        if (now >= quote.ValidUntil)
        {
            throw new LightningSendNotFundableException(
                FundingRefusal.QuoteExpired, "the quote has expired — request a fresh one");
        }

        if (quote.RefundLocktime - now < MinHeadroomSeconds)
        {
            throw new LightningSendNotFundableException(
                FundingRefusal.InsufficientHeadroom,
                $"only {quote.RefundLocktime - now}s remain before the refund path opens, need {MinHeadroomSeconds}s");
        }

        // Exact-out: the invoice fixes the payout, so a quote that pays anything else is not a
        // quote for this invoice. FromAmount is what we lock up, and a solver quoting less than it
        // pays out would be funding the difference itself — a shape worth refusing rather than
        // trusting.
        if (quote.ToAmount != invoiceAmountSats)
        {
            throw new LightningSendNotFundableException(
                FundingRefusal.AmountMismatch,
                $"quote pays {quote.ToAmount} sats, the invoice asks {invoiceAmountSats}");
        }

        if (quote.FromAmount < quote.ToAmount)
        {
            throw new LightningSendNotFundableException(
                FundingRefusal.AmountMismatch,
                $"quote asks {quote.FromAmount} sats for a {quote.ToAmount}-sat payout");
        }
    }

    /// <summary>
    /// Accept whichever of the maker's two derived lockup shapes matches the solver's quoted
    /// address; refuse if neither does.
    /// </summary>
    /// <param name="quote">The quote carrying the compare-only address.</param>
    /// <param name="eightLeaf">The candidate without the timelocked refund leaf.</param>
    /// <param name="nineLeaf">The candidate with it.</param>
    /// <param name="isMainnet">Which network's address encoding to compare under.</param>
    /// <returns>Whichever candidate matched.</returns>
    /// <remarks>
    /// Comparing against both, rather than a single guessed shape, is what makes this corridor
    /// tolerant of a solver on either side of the timelocked refund leaf — nothing on the wire says
    /// which it has deployed. Accepting either is safe because both pin the refund covenant to the
    /// maker's own address; what must never happen is accepting one that matches neither.
    /// </remarks>
    /// <exception cref="LockupAddressMismatchException">The quoted address matches neither candidate.</exception>
    public static VHTLCv2Contract ResolveLockupContract(
        RfqQuote<LightningSendQuoteProfile> quote,
        VHTLCv2Contract eightLeaf,
        VHTLCv2Contract nineLeaf,
        bool isMainnet)
    {
        var quoted = quote.Profile?.LockupAddress;
        var (matched, eightAddress, nineAddress) =
            LightningCorridor.MatchQuotedLockup(eightLeaf, nineLeaf, quoted, isMainnet);

        return matched ?? throw new LockupAddressMismatchException(eightAddress, nineAddress, quoted);
    }
}
