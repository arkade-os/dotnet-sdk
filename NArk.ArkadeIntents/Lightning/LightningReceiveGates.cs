using BTCPayServer.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NBitcoin;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>Why a client refused a receive quote it had already received.</summary>
public enum LightningReceiveRefusalReason
{
    /// <summary>The quote carried no invoice, or one that will not decode.</summary>
    UnusableInvoice,

    /// <summary>The invoice is for a different payment hash than the one requested.</summary>
    WrongPaymentHash,

    /// <summary>The invoice's amount is not the one the quote says the payer owes.</summary>
    AmountMismatch,

    /// <summary>The quote delivers less to us than we asked to receive (exact-out only).</summary>
    ShortPayout,

    /// <summary>The quote bills the payer something other than the amount asked for (exact-in only).</summary>
    PayerChargeMismatch,

    /// <summary>The invoice or the quote has already expired.</summary>
    Expired,

    /// <summary>Too little time between the payment deadline and the solver's reclaim opening.</summary>
    ClaimWindowTooShort,

    /// <summary>The quote bills the payer more than the caller's ceiling allows.</summary>
    PriceTooHigh,
}

/// <summary>Thrown when a client's own checks refuse a receive quote.</summary>
public sealed class LightningReceiveNotUsableException(
    LightningReceiveRefusalReason reason, string message) : Exception(message)
{
    /// <summary>Which check refused. Branch on this, never on the message.</summary>
    public LightningReceiveRefusalReason Reason { get; } = reason;
}

/// <summary>
/// The receive client's checks on what the solver sent back: pure decisions over a quote.
/// </summary>
/// <remarks>
/// Kept free of I/O so each check is testable at its exact boundary, the same way
/// <see cref="LightningSendGates"/> is on the send leg.
/// </remarks>
public static class LightningReceiveGates
{
    /// <summary>
    /// The minimum time the claim window must stay open after the payer's deadline, in seconds.
    /// </summary>
    /// <remarks>
    /// Once the solver's reclaim opens the claim refuses to race it, so the window between "the
    /// payer can still pay" and "the solver can take its lockup back" is the whole opportunity to
    /// take delivery. Sized to the reference client's <c>MIN_CLAIM_WINDOW_SECONDS</c>.
    /// </remarks>
    public const long MinClaimWindowSeconds = 30 * 60;

    /// <summary>
    /// Check the solver's invoice against what was actually asked for, and return it decoded.
    /// </summary>
    /// <param name="quote">The solver's quote.</param>
    /// <param name="expectedPaymentHash">The hash the client requested against, hex.</param>
    /// <param name="requestedSats">The size the client asked for, on the leg <paramref name="amountSide"/> names.</param>
    /// <param name="amountSide">Which leg the client pinned when it asked.</param>
    /// <param name="network">The network to decode on.</param>
    /// <returns>The decoded invoice.</returns>
    /// <exception cref="LightningReceiveNotUsableException">The invoice is missing, wrong or unusable.</exception>
    /// <remarks>
    /// <para>
    /// The invoice is the one thing here a third party acts on, so it is checked rather than
    /// trusted. A wrong payment hash would be an invoice the client's own preimage can never settle
    /// — the payer's money would move and the swap still could not complete.
    /// </para>
    /// <para>
    /// The two amounts are checked against different things, and mixing them up is easy: on this
    /// corridor <c>from_amount</c> is what the PAYER owes, so it is what the invoice must say, while
    /// <c>to_amount</c> is what lands on Arkade. They differ by the solver's fee, so comparing the
    /// invoice against the payout would refuse every quote that charges anything — and comparing the
    /// payout against nothing would accept a quote that quietly delivers less than was asked for.
    /// </para>
    /// <para>
    /// Which of the two the request pinned decides which one is checked against it, and checking the
    /// wrong one refuses every honest quote. An exact-out request fixes the payout and lets the
    /// charge float up by the fee; an exact-in request fixes the charge and lets the payout float
    /// down by it. Holding a quote to the leg the client did not pin would be holding it to a number
    /// nobody agreed on.
    /// </para>
    /// </remarks>
    public static BOLT11PaymentRequest VerifyInvoice(
        RfqQuote<LightningReceiveQuoteProfile> quote,
        string expectedPaymentHash,
        long requestedSats,
        RfqAmountSide amountSide,
        Network network)
    {
        if (quote.Profile?.Invoice is not { Length: > 0 } raw)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.UnusableInvoice, "the quote carried no invoice");
        }

        BOLT11PaymentRequest decoded;
        try
        {
            decoded = BOLT11PaymentRequest.Parse(raw, network);
        }
        catch (Exception e)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.UnusableInvoice,
                $"the quote's invoice will not decode: {e.Message}");
        }

        // ToString() renders big-endian, the order a payment hash is written and compared in;
        // ToBytes() would hand back the reverse and make every comparison here fail.
        var hash = decoded.PaymentHash.ToString().ToLowerInvariant();
        if (hash != expectedPaymentHash.ToLowerInvariant())
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.WrongPaymentHash,
                $"the invoice is for payment hash {hash}, not the requested {expectedPaymentHash} — " +
                "our preimage could never settle it");
        }

        var invoiceSats = decoded.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
        if (invoiceSats != quote.FromAmount)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.AmountMismatch,
                $"the invoice asks the payer for {invoiceSats} sats but the quote says {quote.FromAmount}");
        }

        // Exact-out: the payout is the number that was asked for, and the fee floats the charge up.
        // Permissive by a satoshi on purpose — the spec puts the sub-unit rounding correction in the
        // give, so a conforming quote can deliver a hair more but never less.
        if (amountSide == RfqAmountSide.To && quote.ToAmount < requestedSats)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.ShortPayout,
                $"the quote delivers {quote.ToAmount} sats, less than the {requestedSats} asked for");
        }

        // Exact-in: the charge is the number that was asked for, and the fee floats the payout down.
        // Exact in both directions, unlike the payout check above: a charge below the request
        // under-credits the swap just as surely as one above it overcharges the payer, and on this
        // leg the figure is the one a third party has already agreed to pay.
        if (amountSide == RfqAmountSide.From && quote.FromAmount != requestedSats)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.PayerChargeMismatch,
                $"the quote bills the payer {quote.FromAmount} sats, not the {requestedSats} asked for");
        }

        return decoded;
    }

    /// <summary>
    /// Check the quote's deadlines against the clock, and return when the payer must pay by.
    /// </summary>
    /// <param name="quote">The solver's quote.</param>
    /// <param name="invoice">The decoded invoice the payer will pay.</param>
    /// <param name="now">The current time, unix seconds.</param>
    /// <param name="maxPayAmountSats">
    /// Refuse a quote billing the payer more than this. <c>null</c> for no ceiling.
    /// </param>
    /// <returns>The payment deadline — the earlier of the invoice's expiry and the quote's
    /// <c>valid_until</c> — unix seconds.</returns>
    /// <exception cref="LightningReceiveNotUsableException">A deadline is already past, or the
    /// claim window after it is too short to deliver in.</exception>
    /// <remarks>
    /// Run before the invoice is handed out. The invoice's amounts and hash say WHAT is being paid;
    /// these deadlines say whether the swap can still complete at all: a payer who pays late, or a
    /// lockup the solver can reclaim moments after funding it, turns the swap into money parked in
    /// a held HTLC until it lapses. Checked here rather than at claim time because the payment is
    /// the point of no return — once the payer has the invoice, refusing later helps nobody.
    /// </para>
    /// <para>
    /// <paramref name="maxPayAmountSats"/> bounds the leg the request did not pin. Asking to receive
    /// a fixed amount on Arkade leaves what the payer is billed as the solver's to choose, and this
    /// is the only thing that limits it — which matters to whoever hands that invoice to a customer,
    /// not to the swap, since a refusal here costs nothing and the payout is checked separately.
    /// </para>
    /// </remarks>
    public static long AssertReceivable(
        RfqQuote<LightningReceiveQuoteProfile> quote,
        BOLT11PaymentRequest invoice,
        long now,
        long? maxPayAmountSats = null)
    {
        var payDeadline = Math.Min(invoice.ExpiryDate.ToUnixTimeSeconds(), quote.ValidUntil);
        if (now >= payDeadline)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.Expired,
                "the invoice or the quote has already expired — request a fresh one");
        }

        if (quote.RefundLocktime - payDeadline < MinClaimWindowSeconds)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.ClaimWindowTooShort,
                $"only {quote.RefundLocktime - payDeadline}s to claim after the payment deadline, " +
                $"need {MinClaimWindowSeconds}s");
        }

        if (maxPayAmountSats is { } ceiling && quote.FromAmount > ceiling)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.PriceTooHigh,
                $"the quote bills the payer {quote.FromAmount} sats, above the {ceiling} ceiling");
        }

        return payDeadline;
    }
}
