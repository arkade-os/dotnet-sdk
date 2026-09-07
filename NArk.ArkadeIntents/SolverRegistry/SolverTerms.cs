using NArk.ArkadeIntents.Rfq;

namespace NArk.ArkadeIntents.SolverRegistry;

/// <summary>Why a solver's own published terms rule out a trade, or a quote departs from them.</summary>
public enum SolverTermsRefusal
{
    /// <summary>The solver publishes no market for this corridor.</summary>
    UnservedCorridor,

    /// <summary>Below the smallest size the solver advertises.</summary>
    BelowMinimum,

    /// <summary>Above the largest size the solver advertises.</summary>
    AboveMaximum,

    /// <summary>
    /// The solver serves this market, but not in this direction: it does not pay out the side the
    /// trade would receive.
    /// </summary>
    DirectionNotServed,

    /// <summary>The quote's spread exceeds the fee the card advertises.</summary>
    FeeAboveAdvertised,
}

/// <summary>Thrown when a solver's card rules out a trade, or its quote contradicts that card.</summary>
public sealed class SolverTermsException(SolverTermsRefusal reason, string message) : Exception(message)
{
    /// <summary>Which check refused. Branch on this, never on the message.</summary>
    public SolverTermsRefusal Reason { get; } = reason;
}

/// <summary>
/// Checks a trade against what a solver publicly committed to on its registry card.
/// </summary>
/// <remarks>
/// <para>
/// The card is the only statement of terms that carries provenance — signed, git-reviewed, tied to a
/// discoverable identity — while a quote is whatever arrived on a socket. Comparing one against the
/// other is the only way to notice a solver quoting differently from how it advertises, which no
/// amount of checking the quote against itself can reveal.
/// </para>
/// <para>
/// Checking limits before asking also spares a round trip: a request outside the published range is
/// one the solver refuses anyway, and its refusal cannot say by how much.
/// </para>
/// </remarks>
public static class SolverTerms
{
    /// <summary>Find the market a card serves for a directional pair.</summary>
    /// <param name="card">The solver's card.</param>
    /// <param name="pair">The RFQ pair, e.g. <c>arkade:BTC-&gt;lightning:BTC</c>.</param>
    /// <returns>The market, or <c>null</c> when the solver does not serve it.</returns>
    /// <remarks>
    /// A card states a market key (<c>BTC/lightning:BTC</c>) rather than a direction, because a
    /// solver that serves a pair serves it both ways. Matching therefore ignores direction.
    /// </remarks>
    public static SolverMarket? MarketFor(SolverCard card, string pair) => Resolve(card, pair)?.Market;

    /// <summary>
    /// The market a card serves for a directional pair, and which side the solver pays out.
    /// </summary>
    /// <remarks>
    /// A card states a market key (<c>BTC/lightning:BTC</c>) rather than a direction, because a
    /// solver serving a pair may serve it both ways. The direction lives in the RFQ pair's
    /// <c>to</c> leg, and it decides which of the four bounds applies: the bound is always on the
    /// side the solver pays out, which is the side the maker receives.
    /// </remarks>
    private static (SolverMarket Market, MarketSide Payout)? Resolve(SolverCard card, string pair)
    {
        var (from, to) = SplitPair(pair);
        if (from is null || to is null) return null;

        foreach (var market in card.Markets)
        {
            var rails = market.Pair.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (rails.Length != 2) continue;

            if (Matches(rails[0], from) && Matches(rails[1], to)) return (market, MarketSide.Quote);
            if (Matches(rails[0], to) && Matches(rails[1], from)) return (market, MarketSide.Base);
        }
        return null;
    }

    /// <summary>
    /// Refuse a size the solver's card already rules out, before anything is asked of it.
    /// </summary>
    /// <param name="card">The solver's card.</param>
    /// <param name="pair">The RFQ pair.</param>
    /// <param name="amountSats">The size being traded.</param>
    /// <exception cref="SolverTermsException">The corridor is unserved, or the size is out of range.</exception>
    public static void AssertWithinLimits(SolverCard card, string pair, long amountSats)
    {
        var (market, payout) = Resolve(card, pair)
            ?? throw new SolverTermsException(
                SolverTermsRefusal.UnservedCorridor, $"this solver publishes no market for {pair}");

        // The bound is on the side the solver PAYS OUT — the maker's receiving leg — so which pair
        // of the card's four applies is decided by direction, not by which happens to be non-zero.
        // Sending arkade sats over Lightning is bounded by the quote side; receiving them back is
        // bounded by the base side, and a card whose two sides differ makes the two answers differ.
        var (min, max) = payout == MarketSide.Quote
            ? (market.MinQuoteAmount, market.MaxQuoteAmount)
            : (market.MinBaseAmount, market.MaxBaseAmount);

        // A zero maximum disables the side: the solver does not pay it out, so this direction is
        // not on offer however small the trade.
        if (max <= 0)
        {
            throw new SolverTermsException(
                SolverTermsRefusal.DirectionNotServed,
                $"this solver does not pay out the receiving side of {pair}");
        }

        if (min > 0 && amountSats < min)
        {
            throw new SolverTermsException(
                SolverTermsRefusal.BelowMinimum, $"{amountSats} sats is below this solver's {min} minimum");
        }
        if (amountSats > max)
        {
            throw new SolverTermsException(
                SolverTermsRefusal.AboveMaximum, $"{amountSats} sats is above this solver's {max} maximum");
        }
    }

    /// <summary>
    /// Refuse a quote that charges more than the card advertises.
    /// </summary>
    /// <typeparam name="TProfile">The corridor's quote-profile shape.</typeparam>
    /// <param name="card">The solver's card.</param>
    /// <param name="quote">The quote to check.</param>
    /// <exception cref="SolverTermsException">The spread exceeds the advertised fee.</exception>
    /// <remarks>
    /// <para>
    /// The spread is the fee, so this compares the gap between the two amounts against what the
    /// card commits to: basis points on the amount, plus a flat component where the card declares
    /// one. A rounding satoshi is tolerated because a solver computing the same rate in integer
    /// arithmetic can legitimately land one either side.
    /// </para>
    /// <para>
    /// The flat part matters more than its size suggests. Ignoring it does not make the check
    /// stricter in a useful direction — it makes it refuse quotes that match the card exactly,
    /// turning an honest solver's advertised pricing into a failed swap.
    /// </para>
    /// </remarks>
    public static void AssertFeeWithinAdvertised<TProfile>(SolverCard card, RfqQuote<TProfile> quote)
    {
        if (MarketFor(card, quote.Pair) is not { } market) return;

        var charged = quote.FromAmount - quote.ToAmount;
        if (charged <= 0) return;

        var advertised = market.TotalFeeOn(quote.FromAmount);
        if (charged > advertised + 1)
        {
            var flat = market.FeeFlatAmount > 0 ? $" + {market.FeeFlatAmount} flat" : "";
            throw new SolverTermsException(
                SolverTermsRefusal.FeeAboveAdvertised,
                $"the quote charges {charged} sats, more than the {market.FeeBps} bps{flat} " +
                $"({advertised} sats) this solver advertises");
        }
    }

    private static (string? From, string? To) SplitPair(string pair)
    {
        var sides = pair.Split("->", StringSplitOptions.RemoveEmptyEntries);
        return sides.Length == 2 ? (sides[0].Trim(), sides[1].Trim()) : (null, null);
    }

    /// <summary>
    /// A card's rail names the asset alone for arkade (<c>BTC</c>) and prefixes it otherwise
    /// (<c>lightning:BTC</c>), while an RFQ pair always prefixes.
    /// </summary>
    private static bool Matches(string cardRail, string rfqSide) =>
        string.Equals(cardRail, rfqSide, StringComparison.OrdinalIgnoreCase) ||
        string.Equals("arkade:" + cardRail, rfqSide, StringComparison.OrdinalIgnoreCase);
}
