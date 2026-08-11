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

    /// <summary>The quote delivers less to us than we asked to receive.</summary>
    ShortPayout,
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
/// <see cref="LightningSwapGates"/> is on the send leg.
/// </remarks>
public static class LightningReceiveGates
{
    /// <summary>
    /// Check the solver's invoice against what was actually asked for, and return it decoded.
    /// </summary>
    /// <param name="quote">The solver's quote.</param>
    /// <param name="expectedPaymentHash">The hash the client requested against, hex.</param>
    /// <param name="requestedPayoutSats">What the client asked to receive on Arkade.</param>
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
    /// </remarks>
    public static BOLT11PaymentRequest VerifyInvoice(
        RfqQuote<LightningReceiveQuoteProfile> quote,
        string expectedPaymentHash,
        long requestedPayoutSats,
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

        if (quote.ToAmount < requestedPayoutSats)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.ShortPayout,
                $"the quote delivers {quote.ToAmount} sats, less than the {requestedPayoutSats} asked for");
        }

        return decoded;
    }
}
