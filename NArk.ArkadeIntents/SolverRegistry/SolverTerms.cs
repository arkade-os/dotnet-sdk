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
    public static SolverMarket? MarketFor(SolverCard card, string pair)
    {
        var (from, to) = SplitPair(pair);
        if (from is null || to is null) return null;

        return card.Markets.FirstOrDefault(m =>
        {
            var rails = m.Pair.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return rails.Length == 2 &&
                   ((Matches(rails[0], from) && Matches(rails[1], to)) ||
                    (Matches(rails[0], to) && Matches(rails[1], from)));
        });
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
        var market = MarketFor(card, pair)
            ?? throw new SolverTermsException(
                SolverTermsRefusal.UnservedCorridor, $"this solver publishes no market for {pair}");

        // A corridor states its bounds on the quote side and leaves the base side at zero, so a zero
        // is "unstated" rather than "no trade is large enough".
        var min = market.MinQuoteAmount > 0 ? market.MinQuoteAmount : market.MinBaseAmount;
        var max = market.MaxQuoteAmount > 0 ? market.MaxQuoteAmount : market.MaxBaseAmount;

        if (min > 0 && amountSats < min)
        {
            throw new SolverTermsException(
                SolverTermsRefusal.BelowMinimum, $"{amountSats} sats is below this solver's {min} minimum");
        }
        if (max > 0 && amountSats > max)
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

        var advertised = quote.FromAmount * market.FeeBps / 10_000 + market.FeeFlatAmount;
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
