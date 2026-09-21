using System.Text.Json;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.ArkadeIntents.SolverRegistry;

namespace NArk.Tests.ArkadeIntents;

/// <summary>
/// Holding a solver to the terms it published.
/// </summary>
/// <remarks>
/// A quote is whatever arrived on a socket; the card is signed, reviewed and tied to a discoverable
/// identity. Checking one against the other is the only way to catch a solver quoting differently
/// from how it advertises — no amount of checking a quote against itself can reveal that.
/// </remarks>
[TestFixture]
public class SolverTermsTests
{
    private const string SendPair = "arkade:BTC->lightning:BTC";
    private const string ReceivePair = "lightning:BTC->arkade:BTC";

    /// <summary>The shape the reference solver publishes: a market key, not a direction.</summary>
    private const string CardJson = """
    {
      "version": 0,
      "name": "testsolver",
      "markets": [
        {
          "pair": "BTC/lightning:BTC",
          "base_asset": { "id": "btc", "decimals": 8 },
          "quote_asset": { "id": "btc", "decimals": 8 },
          "quote_corridor": "lightning",
          "fee_bps": 30,
          "min_base_amount": "0",
          "max_base_amount": "0",
          "min_quote_amount": "1000",
          "max_quote_amount": "1000000"
        }
      ]
    }
    """;

    [Test]
    public void AMarketKeyMatchesBothDirections()
    {
        // A card states BTC/lightning:BTC once; a solver serving that pair serves it either way, so
        // matching on direction would make one of the two corridors look unserved.
        Assert.Multiple(() =>
        {
            Assert.That(SolverTerms.MarketFor(Card(), SendPair), Is.Not.Null);
            Assert.That(SolverTerms.MarketFor(Card(), ReceivePair), Is.Not.Null);
        });
    }

    [Test]
    public void AnUnservedCorridor_IsRefusedBeforeAsking()
    {
        var ex = Assert.Throws<SolverTermsException>(() =>
            SolverTerms.AssertWithinLimits(Card(), "arkade:BTC->onchain:BTC", 50_000));

        Assert.That(ex!.Reason, Is.EqualTo(SolverTermsRefusal.UnservedCorridor));
    }

    [TestCase(999, SolverTermsRefusal.BelowMinimum)]
    [TestCase(1_000_001, SolverTermsRefusal.AboveMaximum)]
    public void ASizeOutsideTheAdvertisedRange_IsRefused(long amount, SolverTermsRefusal expected)
    {
        var ex = Assert.Throws<SolverTermsException>(() =>
            SolverTerms.AssertWithinLimits(Card(), SendPair, amount));

        Assert.That(ex!.Reason, Is.EqualTo(expected));
    }

    [TestCase(1000)]
    [TestCase(50_000)]
    [TestCase(1_000_000)]
    public void ASizeInsideTheRange_IsAccepted(long amount)
    {
        Assert.DoesNotThrow(() => SolverTerms.AssertWithinLimits(Card(), SendPair, amount));
    }

    /// <summary>
    /// The card the reference Lightning solver actually publishes: both directions served, and the
    /// two sides bounded differently.
    /// </summary>
    private const string AsymmetricCardJson = """
    {
      "version": 0,
      "name": "ln-solver",
      "markets": [
        {
          "pair": "BTC/lightning:BTC",
          "base_asset": { "id": "btc", "decimals": 8 },
          "quote_asset": { "id": "btc", "decimals": 8 },
          "quote_corridor": "lightning",
          "fee_bps": 30,
          "min_base_amount": "1000",
          "max_base_amount": "50000",
          "min_quote_amount": "1000",
          "max_quote_amount": "25000"
        }
      ]
    }
    """;

    [Test]
    public void TheBoundIsOnTheSideTheSolverPaysOut()
    {
        // 30 000 sats: over the 25 000 the solver pays out over Lightning, inside the 50 000 it pays
        // out on Arkade. One card, one size, two answers — and reading the same side for both would
        // refuse a receive this solver serves.
        var card = Card(AsymmetricCardJson);

        Assert.Multiple(() =>
        {
            Assert.That(
                Assert.Throws<SolverTermsException>(
                    () => SolverTerms.AssertWithinLimits(card, SendPair, 30_000))!.Reason,
                Is.EqualTo(SolverTermsRefusal.AboveMaximum));
            Assert.DoesNotThrow(() => SolverTerms.AssertWithinLimits(card, ReceivePair, 30_000));
        });
    }

    [Test]
    public void AnExactInputIsCheckedInItsOwnAtomicUnit_NotTheCrossAssetPayoutUnit()
    {
        var card = Card(AsymmetricCardJson);

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => SolverTerms.AssertInputWithinLimits(card, SendPair, 30_000));
            Assert.That(Assert.Throws<SolverTermsException>(
                    () => SolverTerms.AssertInputWithinLimits(card, SendPair, 50_001))!.Reason,
                Is.EqualTo(SolverTermsRefusal.AboveMaximum));
            Assert.That(Assert.Throws<SolverTermsException>(
                    () => SolverTerms.AssertInputWithinLimits(card, ReceivePair, 30_000))!.Reason,
                Is.EqualTo(SolverTermsRefusal.AboveMaximum));
        });
    }

    [Test]
    public void ADisabledSide_RefusesThatDirectionAtAnySize()
    {
        // CardJson zeroes the base side, so this solver never pays out on Arkade: receiving from
        // Lightning is not a small trade away from working, it is not on offer.
        var ex = Assert.Throws<SolverTermsException>(
            () => SolverTerms.AssertWithinLimits(Card(), ReceivePair, 1000));

        Assert.That(ex!.Reason, Is.EqualTo(SolverTermsRefusal.DirectionNotServed));
    }

    [Test]
    public void AQuoteChargingMoreThanAdvertised_IsRefused()
    {
        // 30 bps on 50 000 is 150 sats. Charging 500 is a solver not honouring its own card.
        var ex = Assert.Throws<SolverTermsException>(() =>
            SolverTerms.AssertFeeWithinAdvertised(Card(), Quote(from: 50_000, to: 49_500)));

        Assert.That(ex!.Reason, Is.EqualTo(SolverTermsRefusal.FeeAboveAdvertised));
    }

    [Test]
    public void AQuoteChargingTheAdvertisedFee_IsAccepted()
    {
        Assert.DoesNotThrow(() =>
            SolverTerms.AssertFeeWithinAdvertised(Card(), Quote(from: 50_000, to: 49_850)));
    }

    [Test]
    public void ARoundingSatoshi_IsTolerated()
    {
        // Both sides compute the same rate in integer arithmetic and can legitimately land either
        // side of the boundary. Refusing over one satoshi would reject honest quotes.
        Assert.DoesNotThrow(() =>
            SolverTerms.AssertFeeWithinAdvertised(Card(), Quote(from: 50_000, to: 49_849)));
    }

    [Test]
    public void AFlatFeeTheCardDeclares_IsAllowedOnTopOfTheSpread()
    {
        // 30 bps on 50 000 is 150, plus a declared 100 flat. A solver quoting exactly what it
        // advertises must not be refused: ignoring the flat part does not make this check stricter
        // in any useful direction, it makes it reject honest pricing and fail the swap.
        Assert.DoesNotThrow(() =>
            SolverTerms.AssertFeeWithinAdvertised(CardWithFlatFee(), Quote(from: 50_000, to: 49_750)));
    }

    [Test]
    public void AFlatFeeIsAnAllowance_NotABlankCheque()
    {
        // Still bounded: the flat component widens the allowance by exactly what the card declares
        // and not by more.
        var ex = Assert.Throws<SolverTermsException>(() =>
            SolverTerms.AssertFeeWithinAdvertised(CardWithFlatFee(), Quote(from: 50_000, to: 49_500)));

        Assert.That(ex!.Reason, Is.EqualTo(SolverTermsRefusal.FeeAboveAdvertised));
    }

    [Test]
    public void ACardWithoutAFlatFee_ChargesNoneImplicitly()
    {
        // An absent fee_flat and a declared zero are the same charge — cards written before the
        // field existed must keep working.
        var ex = Assert.Throws<SolverTermsException>(() =>
            SolverTerms.AssertFeeWithinAdvertised(Card(), Quote(from: 50_000, to: 49_750)));

        Assert.That(ex!.Reason, Is.EqualTo(SolverTermsRefusal.FeeAboveAdvertised));
    }

    [Test]
    public void AFreeQuote_IsAccepted()
    {
        // Charging nothing is always within an advertised maximum, including on a card that
        // advertises a fee.
        Assert.DoesNotThrow(() =>
            SolverTerms.AssertFeeWithinAdvertised(Card(), Quote(from: 50_000, to: 50_000)));
    }

    [Test]
    public void AQuoteForAnUnservedPair_IsNotJudged()
    {
        // Nothing to compare against. Refusing here would be inventing a term the card never stated.
        Assert.DoesNotThrow(() => SolverTerms.AssertFeeWithinAdvertised(
            Card(), Quote(from: 50_000, to: 1, pair: "arkade:BTC->onchain:BTC")));
    }

    [Test]
    public void AnAmountWiderThanSatoshis_IsCheckedRatherThanOverflowing()
    {
        // One whole unit of an 18-decimal asset is already 10^18, so a pair of them is past what a
        // satoshi-shaped bound could hold. Overflowing here would read as a broken SDK rather than
        // as a size this solver would happily have quoted.
        var card = Card(CardJson
            .Replace("\"max_quote_amount\": \"1000000\"", "\"max_quote_amount\": \"20000000000000000000\""));
        var amount = System.Numerics.BigInteger.Parse("10000000000000000000");

        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => SolverTerms.AssertWithinLimits(card, SendPair, amount));
            Assert.That(() => SolverTerms.AssertWithinLimits(card, SendPair, amount * 3),
                Throws.TypeOf<SolverTermsException>());
        });
    }

    [Test]
    public void ADirectionalCard_PricesTheDirectionBeingSwapped()
    {
        // `solver_fee` is keyed by the side DEPOSITED. A send deposits the base leg, so it is held
        // to the base entry's 30 bps — the quote entry's 10 would refuse an honest quote here.
        var card = DirectionalCard();

        Assert.Multiple(() =>
        {
            // 50_000 * 30bps = 150, plus the 20 flat this direction states.
            Assert.DoesNotThrow(() => SolverTerms.AssertFeeWithinAdvertised(card, Quote(50_170, 50_000)));
            Assert.That(() => SolverTerms.AssertFeeWithinAdvertised(card, Quote(50_400, 50_000)),
                Throws.TypeOf<SolverTermsException>());
        });
    }

    [Test]
    public void ADirectionalCard_HoldsTheOtherDirectionToItsOwnRate()
    {
        // The receive leg deposits the QUOTE side: 10 bps and no flat, so the send leg's allowance
        // is well over what this direction may charge.
        var card = DirectionalCard();

        Assert.Multiple(() =>
        {
            Assert.That(() => SolverTerms.AssertFeeWithinAdvertised(card, Quote(50_170, 50_000, ReceivePair)),
                Throws.TypeOf<SolverTermsException>());
            // 50_000 * 10bps = 50, and nothing flat.
            Assert.DoesNotThrow(
                () => SolverTerms.AssertFeeWithinAdvertised(card, Quote(50_050, 50_000, ReceivePair)));
        });
    }

    [Test]
    public void ADirectionWithNoEntry_TakesTheMarketRateAndNoFlat()
    {
        // A market publishing per-direction fees states every flat charge it makes, so a direction
        // it leaves out charges none — inheriting the legacy `fee_flat` would bill this direction
        // for the other one's.
        var market = SolverTerms.MarketFor(DirectionalCard("""
            "solver_fee": { "base": { "flat": "20" } },
            """), SendPair)!;

        Assert.Multiple(() =>
        {
            Assert.That(market.FeeBpsOn(MarketSide.Quote), Is.EqualTo(30));
            Assert.That(market.FeeFlatOn(MarketSide.Quote), Is.EqualTo(System.Numerics.BigInteger.Zero));
        });
    }

    [Test]
    public void ACardWithoutDirectionalFees_KeepsTheSingleAdvertisedRate()
    {
        // The overwhelming majority of cards, and the same-asset corridors always: one rate stated
        // for both directions, and the legacy flat applying to whichever one is asked about.
        var market = SolverTerms.MarketFor(CardWithFlatFee(), SendPair)!;

        Assert.Multiple(() =>
        {
            Assert.That(market.FeeBpsOn(MarketSide.Base), Is.EqualTo(30));
            Assert.That(market.FeeFlatOn(MarketSide.Base), Is.EqualTo(new System.Numerics.BigInteger(100)));
            Assert.That(market.FeeFlatOn(MarketSide.Quote), Is.EqualTo(new System.Numerics.BigInteger(100)));
        });
    }

    /// <summary>The card with per-direction fees: 30 bps plus 20 flat one way, 10 bps the other.</summary>
    private static SolverCard DirectionalCard(string solverFee = """
        "solver_fee": { "base": { "bps": 30, "flat": "20" }, "quote": { "bps": 10 } },
        """) =>
        // `fee_bps` stays the WIDEST of the two, which is what the registry requires and what a
        // reader predating `solver_fee` prices with.
        Card(CardJson.Replace("\"fee_bps\": 30", solverFee.Trim() + "\n      \"fee_bps\": 30"));

    private static SolverCard Card(string json = CardJson) =>
        JsonSerializer.Deserialize<SolverCard>(
            json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

    /// <summary>The same card, with a flat component alongside the basis points.</summary>
    private static SolverCard CardWithFlatFee() =>
        Card(CardJson.Replace("\"fee_bps\": 30", "\"fee_bps\": 30,\n      \"fee_flat\": \"100\""));

    private static RfqQuote<LightningSendQuoteProfile> Quote(long from, long to, string pair = SendPair) => new()
    {
        RfqId = new string('9', 64),
        Pair = pair,
        FromAmount = from,
        ToAmount = to,
        SolverPubkey = new string('e', 64),
        ValidUntil = 1_800_000_900,
        RefundLocktime = 1_800_605_184,
    };
}
