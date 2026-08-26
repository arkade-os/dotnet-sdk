using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;

namespace NArk.Tests.ArkadeIntents.Rfq;

/// <summary>
/// Narrowing a solver's reply to the quote that was actually asked for.
/// </summary>
/// <remarks>
/// Everything downstream — the gates, the derivation, the funding — reads this quote as though it
/// answers the request. Nothing later re-checks that it does, so a reply admitted here is admitted
/// for good.
/// </remarks>
[TestFixture]
public class ExpectQuoteTests
{
    private const string RfqId = "1f2e3d4c";
    private const string Pair = LightningSendProfile.Pair;

    [Test]
    public void AQuoteForTheRequestedNegotiationAndMarket_IsAccepted()
    {
        var quote = RfqProtocol.ExpectQuote<object>(Reply(RfqId, Pair), RfqId, Pair);

        Assert.That(quote.Pair, Is.EqualTo(Pair));
    }

    [Test]
    public void AQuoteAnsweringAnotherNegotiation_IsRefused()
    {
        // On a shared relay every one of the solver's events arrives on the same subscription.
        Assert.Throws<InvalidOperationException>(
            () => RfqProtocol.ExpectQuote<object>(Reply("deadbeef", Pair), RfqId, Pair));
    }

    [Test]
    public void AQuoteForAnotherMarket_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(
            () => RfqProtocol.ExpectQuote<object>(
                Reply(RfqId, LightningReceiveProfile.Pair), RfqId, Pair));
    }

    [Test]
    public void ARespelledPair_IsRefused()
    {
        // Compared byte for byte, the way the solver compares it: a solver that normalises case is
        // otherwise indistinguishable from one quoting the market that was asked for.
        Assert.Throws<InvalidOperationException>(
            () => RfqProtocol.ExpectQuote<object>(Reply(RfqId, Pair.ToUpperInvariant()), RfqId, Pair));
    }

    [Test]
    public void NoRequestedPair_AcceptsWhateverTheQuoteNames()
    {
        var quote = RfqProtocol.ExpectQuote<object>(Reply(RfqId, "arkade:BTC->onchain:BTC"), RfqId);

        Assert.That(quote.Pair, Is.EqualTo("arkade:BTC->onchain:BTC"));
    }

    [Test]
    public void ARefusal_ThrowsItsReason()
    {
        var refusal = new JsonObject
        {
            ["type"] = "rfq_refusal",
            ["rfq_id"] = RfqId,
            ["reason"] = "rate_limited",
        };

        var ex = Assert.Throws<RfqRefusedException>(
            () => RfqProtocol.ExpectQuote<object>(refusal, RfqId, Pair));

        Assert.That(ex!.Reason, Is.EqualTo(RfqRefusalReason.RateLimited));
    }

    private static JsonNode Reply(string rfqId, string pair) => new JsonObject
    {
        ["type"] = "rfq_quote",
        ["rfq_id"] = rfqId,
        ["pair"] = pair,
        ["solver_pubkey"] = new string('e', 64),
        ["from_amount"] = "50000",
        ["to_amount"] = "49850",
        ["valid_until"] = 1787600000,
        ["refund_locktime"] = 1787700000,
    };
}
