using System.Numerics;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Asset;

namespace NArk.ArkadeIntents.Assets;

/// <summary>Why a client refused to fund an Arkade-to-Arkade quote it had already received.</summary>
public enum ArkadeSwapRefusal
{
    /// <summary>The quote's <c>valid_until</c> has passed, or it carried none.</summary>
    QuoteExpired,

    /// <summary>An amount is missing, non-positive, or not the one that was asked for.</summary>
    AmountRejected,

    /// <summary>The quote answers a different market than the one requested.</summary>
    WrongPair,
}

/// <summary>Thrown when a client's own gates refuse to fund an Arkade-to-Arkade quote.</summary>
public sealed class ArkadeSwapNotFundableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Which gate refused.</param>
    /// <param name="message">A human-readable elaboration.</param>
    public ArkadeSwapNotFundableException(ArkadeSwapRefusal reason, string message) : base(message)
        => Reason = reason;

    /// <summary>Which gate refused. Branch on this, never on the message.</summary>
    public ArkadeSwapRefusal Reason { get; }
}

/// <summary>Thrown when the solver's offer address matches neither the address nor the script we derive.</summary>
/// <remarks>
/// Never fund past this. The offer covenant is derived entirely from the quote's <c>to_amount</c>
/// and the two parameters the client supplied itself, so a disagreement means one side read
/// something the other did not send — and funding it would put a deposit at a script the solver is
/// not watching and the covenant may not release.
/// </remarks>
public sealed class OfferAddressMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="derivedAddress">The address the client derived.</param>
    /// <param name="derivedPkScript">The scriptPubKey the client derived, hex.</param>
    /// <param name="quotedAddress">The address the solver quoted, if any.</param>
    /// <param name="quotedPkScript">The scriptPubKey the solver quoted, if any.</param>
    public OfferAddressMismatchException(
        string derivedAddress, string derivedPkScript, string? quotedAddress, string? quotedPkScript)
        : base("the solver's offer does not match ours — refusing to fund. " +
               $"derived {derivedAddress} ({derivedPkScript}), " +
               $"quoted {quotedAddress ?? "<none>"} ({quotedPkScript ?? "<none>"})")
    {
        DerivedAddress = derivedAddress;
        DerivedPkScript = derivedPkScript;
        QuotedAddress = quotedAddress;
        QuotedPkScript = quotedPkScript;
    }

    /// <summary>What the client derived.</summary>
    public string DerivedAddress { get; }

    /// <summary>The scriptPubKey the client derived, hex.</summary>
    public string DerivedPkScript { get; }

    /// <summary>What the solver claimed, if anything.</summary>
    public string? QuotedAddress { get; }

    /// <summary>The scriptPubKey the solver claimed, if anything.</summary>
    public string? QuotedPkScript { get; }
}

/// <summary>
/// The client's safety gates for the atomic class: pure decisions over a quote and a clock.
/// </summary>
/// <remarks>
/// Shorter than the HTLC corridors' gates, and for a structural reason rather than an oversight.
/// There is no timelock on this class — neither <c>fulfill</c> nor <c>cancel</c> carries one — so
/// there is no headroom to check and no refund deadline to stay behind. The covenant itself is what
/// makes the swap safe: it releases the deposit only to a transaction that pays the quoted amount to
/// the client's own script. What is left to check is that the terms are the ones that were asked
/// for, that they have not lapsed, and that the offer is the client's own derivation.
/// </remarks>
public static class ArkadeSwapGates
{
    /// <summary>
    /// Refuse to fund unless the quote still binds and says what it was asked to say.
    /// </summary>
    /// <param name="quote">The solver's quote.</param>
    /// <param name="requestedPair">The pair that was asked for.</param>
    /// <param name="requestedAmount">The amount that was asked for, in atomic units.</param>
    /// <param name="amountSide">Which leg <paramref name="requestedAmount"/> fixed.</param>
    /// <param name="now">The current time, unix seconds.</param>
    /// <param name="maxFromAmount">An optional cap on what the client will deposit.</param>
    /// <param name="minToAmount">An optional floor on what the client will accept.</param>
    /// <exception cref="ArkadeSwapNotFundableException">A gate refused.</exception>
    /// <remarks>
    /// The two optional bounds are the useful ones, and they bound the side the <b>solver</b> chose.
    /// The named side comes back verbatim — asserting it proves nothing about the solver's pricing —
    /// so a caller that sets neither is one that funds whatever it is asked for.
    /// </remarks>
    public static void AssertFundable(
        RfqQuote<ArkadeSwapQuoteProfile> quote,
        string requestedPair,
        BigInteger requestedAmount,
        RfqAmountSide amountSide,
        long now,
        BigInteger? maxFromAmount = null,
        BigInteger? minToAmount = null)
    {
        if (quote.Pair != requestedPair)
        {
            throw new ArkadeSwapNotFundableException(
                ArkadeSwapRefusal.WrongPair,
                $"the quote answers '{quote.Pair ?? "(none)"}', not the requested '{requestedPair}'");
        }

        if (quote.ValidUntil <= 0)
        {
            throw new ArkadeSwapNotFundableException(
                ArkadeSwapRefusal.QuoteExpired, "the quote carries no valid_until");
        }
        if (now >= quote.ValidUntil)
        {
            // There is no refund path here, so a lapsed quote is not merely stale terms: funding it
            // leaves a deposit only a cooperative cancel can reclaim.
            throw new ArkadeSwapNotFundableException(
                ArkadeSwapRefusal.QuoteExpired,
                $"the quote lapsed at {quote.ValidUntil} (now {now}) — request a fresh one");
        }

        if (quote.FromAtomicAmount <= BigInteger.Zero || quote.ToAtomicAmount <= BigInteger.Zero)
        {
            throw new ArkadeSwapNotFundableException(
                ArkadeSwapRefusal.AmountRejected, "the quote carries a non-positive amount");
        }

        var named = amountSide == RfqAmountSide.From ? quote.FromAtomicAmount : quote.ToAtomicAmount;
        if (named != requestedAmount)
        {
            throw new ArkadeSwapNotFundableException(
                ArkadeSwapRefusal.AmountRejected,
                $"the quote's {(amountSide == RfqAmountSide.From ? "from" : "to")}_amount is {named}, " +
                $"not the requested {requestedAmount}");
        }

        if (maxFromAmount is { } cap && quote.FromAtomicAmount > cap)
        {
            throw new ArkadeSwapNotFundableException(
                ArkadeSwapRefusal.AmountRejected,
                $"the quote asks {quote.FromAtomicAmount}, above the {cap} this caller will deposit");
        }
        if (minToAmount is { } floor && quote.ToAtomicAmount < floor)
        {
            throw new ArkadeSwapNotFundableException(
                ArkadeSwapRefusal.AmountRejected,
                $"the quote pays {quote.ToAtomicAmount}, below the {floor} this caller will accept");
        }
    }

    /// <summary>
    /// Accept the solver's offer only when it is, byte for byte, the one the client derived.
    /// </summary>
    /// <param name="quote">The quote carrying the compare-only offer.</param>
    /// <param name="derived">The client's own derivation.</param>
    /// <exception cref="OfferAddressMismatchException">Either value disagrees.</exception>
    /// <remarks>
    /// Both values are compared, not just the address: the address is what the wallet sends to and
    /// the script is what the covenant compiles to, and a solver agreeing about one while differing
    /// about the other has not derived the same contract.
    /// </remarks>
    public static void AssertOfferIsOurs(RfqQuote<ArkadeSwapQuoteProfile> quote, CreatedOffer derived)
    {
        var derivedPkScript = Convert.ToHexString(derived.SwapPkScript).ToLowerInvariant();
        var quotedAddress = quote.Profile?.OfferAddress;
        var quotedPkScript = quote.Profile?.OfferPkScript;

        if (!string.Equals(quotedAddress, derived.Address, StringComparison.Ordinal)
            || !string.Equals(quotedPkScript, derivedPkScript, StringComparison.OrdinalIgnoreCase))
        {
            throw new OfferAddressMismatchException(
                derived.Address, derivedPkScript, quotedAddress, quotedPkScript);
        }
    }
}
