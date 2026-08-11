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
          "base_asset": { "id": "btc", "precision": 8 },
          "quote_asset": { "id": "btc", "precision": 8 },
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

    private static SolverCard Card() =>
        JsonSerializer.Deserialize<SolverCard>(
            CardJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

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
