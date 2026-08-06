using NArk.ArkadeIntents.Lightning.Rfq;

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
public sealed class LightningSwapNotFundableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Which gate refused.</param>
    /// <param name="message">A human-readable elaboration.</param>
    public LightningSwapNotFundableException(FundingRefusal reason, string message) : base(message)
        => Reason = reason;

    /// <summary>Which gate refused. Branch on this, never on the message.</summary>
    public FundingRefusal Reason { get; }
}

/// <summary>Thrown when the solver's address does not match the maker's own derivation.</summary>
/// <remarks>
/// Never fund past this. It is the single check that makes a wrong or malicious solver able to
/// produce only an address the maker declines, rather than one that traps funds.
/// </remarks>
public sealed class LockupAddressMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="derived">The address the maker derived from its own data.</param>
    /// <param name="quoted">The address the solver sent for comparison.</param>
    public LockupAddressMismatchException(string derived, string? quoted)
        : base("solver's lockup address does not match the local derivation — refusing to fund")
    {
        Derived = derived;
        Quoted = quoted;
    }

    /// <summary>What the maker derived.</summary>
    public string Derived { get; }

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
public static class LightningSwapGates
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
    /// <exception cref="LightningSwapNotFundableException">A gate refused.</exception>
    public static void AssertFundable(RfqQuote quote, long invoiceAmountSats, long invoiceExpiresAt, long now)
    {
        if (now >= invoiceExpiresAt)
        {
            throw new LightningSwapNotFundableException(
                FundingRefusal.InvoiceExpired, "the invoice has expired");
        }

        if (now >= quote.ValidUntil)
        {
            throw new LightningSwapNotFundableException(
                FundingRefusal.QuoteExpired, "the quote has expired — request a fresh one");
        }

        if (quote.RefundLocktime - now < MinHeadroomSeconds)
        {
            throw new LightningSwapNotFundableException(
                FundingRefusal.InsufficientHeadroom,
                $"only {quote.RefundLocktime - now}s remain before the refund path opens, need {MinHeadroomSeconds}s");
        }

        // Exact-out: the invoice fixes the payout, so a quote that pays anything else is not a
        // quote for this invoice. FromAmount is what we lock up, and a solver quoting less than it
        // pays out would be funding the difference itself — a shape worth refusing rather than
        // trusting.
        if (quote.ToAmount != invoiceAmountSats)
        {
            throw new LightningSwapNotFundableException(
                FundingRefusal.AmountMismatch,
                $"quote pays {quote.ToAmount} sats, the invoice asks {invoiceAmountSats}");
        }

        if (quote.FromAmount < quote.ToAmount)
        {
            throw new LightningSwapNotFundableException(
                FundingRefusal.AmountMismatch,
                $"quote asks {quote.FromAmount} sats for a {quote.ToAmount}-sat payout");
        }
    }

    /// <summary>
    /// Compare the solver's address against the maker's own derivation.
    /// </summary>
    /// <param name="quote">The quote carrying the compare-only address.</param>
    /// <param name="derivedAddress">The address the maker derived locally.</param>
    /// <returns><paramref name="derivedAddress"/>, so calls can chain.</returns>
    /// <exception cref="LockupAddressMismatchException">The two disagree.</exception>
    public static string VerifyLockupAddress(RfqQuote quote, string derivedAddress)
    {
        var quoted = quote.Profile?.LockupAddress;
        if (!string.Equals(derivedAddress, quoted, StringComparison.Ordinal))
        {
            throw new LockupAddressMismatchException(derivedAddress, quoted);
        }
        return derivedAddress;
    }
}
