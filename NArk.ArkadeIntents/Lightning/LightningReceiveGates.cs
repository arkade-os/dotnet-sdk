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

    /// <summary>The invoice's amount is not what the quote promised to deliver.</summary>
    AmountMismatch,
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
    /// <param name="network">The network to decode on.</param>
    /// <returns>The decoded invoice.</returns>
    /// <exception cref="LightningReceiveNotUsableException">The invoice is missing, wrong or unusable.</exception>
    /// <remarks>
    /// The invoice is the one thing here a third party acts on, so it is checked rather than
    /// trusted. A wrong payment hash would be an invoice the client's own preimage can never settle
    /// — the payer's money would move and the swap still could not complete. A wrong amount would
    /// have the payer send a sum the swap does not deliver.
    /// </remarks>
    public static BOLT11PaymentRequest VerifyInvoice(
        RfqQuote<LightningReceiveQuoteProfile> quote, string expectedPaymentHash, Network network)
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

        var hash = Convert.ToHexString(decoded.PaymentHash.ToBytes()).ToLowerInvariant();
        if (hash != expectedPaymentHash.ToLowerInvariant())
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.WrongPaymentHash,
                $"the invoice is for payment hash {hash}, not the requested {expectedPaymentHash} — " +
                "our preimage could never settle it");
        }

        var invoiceSats = decoded.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
        if (invoiceSats != quote.ToAmount)
        {
            throw new LightningReceiveNotUsableException(
                LightningReceiveRefusalReason.AmountMismatch,
                $"the invoice asks for {invoiceSats} sats but the quote delivers {quote.ToAmount}");
        }

        return decoded;
    }
}
