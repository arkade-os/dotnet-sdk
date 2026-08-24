using System.Text.Json;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;

namespace NArk.Tests.ArkadeIntents.Rfq;

/// <summary>
/// How amounts are written to and read from the RFQ wire (protocol § 2.1).
/// </summary>
/// <remarks>
/// The encoding is the whole subject here, not the arithmetic: a quote read one order of magnitude
/// out funds one order of magnitude out, and every check downstream would be comparing the wrong
/// number against the wrong number and agreeing.
/// </remarks>
[TestFixture]
public class RfqAmountEncodingTests
{
    [Test]
    public void Request_WritesTheAmountAsADecimalString()
    {
        var request = LightningReceiveProfile.Request(
            50_000, RfqAmountSide.To, new string('a', 64), "ark1payout", new string('b', 64), "packet",
            new string('c', 64));

        var json = JsonSerializer.SerializeToNode(request, RfqProtocol.Json)!;

        Assert.That(json["amount"]!.GetValue<string>(), Is.EqualTo("50000"));
    }

    [Test]
    public void Quote_ReadsTheDecimalStringForm()
    {
        var quote = ReadQuote("\"50000\"", "\"49875\"");

        Assert.Multiple(() =>
        {
            Assert.That(quote.FromAmount, Is.EqualTo(50_000));
            Assert.That(quote.ToAmount, Is.EqualTo(49_875));
        });
    }

    [Test]
    public void Quote_StillReadsTheJsonNumberFormSolversEmitToday()
    {
        var quote = ReadQuote("50000", "49875");

        Assert.Multiple(() =>
        {
            Assert.That(quote.FromAmount, Is.EqualTo(50_000));
            Assert.That(quote.ToAmount, Is.EqualTo(49_875));
        });
    }

    /// <remarks>
    /// Each spelling is one a sender might reach for, and each would misprice by a different amount
    /// if it were quietly accepted.
    /// </remarks>
    [TestCase("\"1e5\"", TestName = "Quote_RefusesExponentNotation")]
    [TestCase("\"1.5\"", TestName = "Quote_RefusesADecimalPoint")]
    [TestCase("\"-50000\"", TestName = "Quote_RefusesASign")]
    [TestCase("\" 50000\"", TestName = "Quote_RefusesLeadingWhitespace")]
    [TestCase("\"050000\"", TestName = "Quote_RefusesALeadingZero")]
    [TestCase("\"\"", TestName = "Quote_RefusesAnEmptyString")]
    public void Quote_RefusesANonCanonicalAmount(string fromAmount)
    {
        Assert.Throws<JsonException>(() => ReadQuote(fromAmount, "\"49875\""));
    }

    [Test]
    public void Quote_RefusesANumberBeyondWhatAJsonNumberCarriesExactly()
    {
        // 2^53, the first integer a double cannot distinguish from its neighbour. A counterparty
        // cannot have produced this reliably, so reading it would be funding a figure neither side
        // can prove was the one quoted.
        Assert.Throws<JsonException>(() => ReadQuote("9007199254740992", "\"49875\""));
    }

    [Test]
    public void Quote_ReadsTheSameLargeAmountWhenItIsSentAsAString()
    {
        var quote = ReadQuote("\"9007199254740992\"", "\"49875\"");

        Assert.That(quote.FromAmount, Is.EqualTo(9007199254740992L));
    }

    private static RfqQuote<LightningReceiveQuoteProfile> ReadQuote(string fromAmount, string toAmount)
    {
        var json = $$"""
                   {
                     "v": 1,
                     "type": "rfq_quote",
                     "rfq_id": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                     "pair": "lightning:BTC->arkade:BTC",
                     "from_amount": {{fromAmount}},
                     "to_amount": {{toAmount}},
                     "solver_pubkey": "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                     "valid_until": 1800000900,
                     "refund_locktime": 1800605184
                   }
                   """;

        return JsonSerializer.Deserialize<RfqQuote<LightningReceiveQuoteProfile>>(json, RfqProtocol.Json)!;
    }
}
