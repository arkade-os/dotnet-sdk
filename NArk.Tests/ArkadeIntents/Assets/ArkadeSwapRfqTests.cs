using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Assets;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Asset;
using NArk.Core.Assets;

namespace NArk.Tests.ArkadeIntents.Assets;

/// <summary>
/// The <c>arkade:X-&gt;arkade:Y</c> profile on the wire, and the gates that stand between a quote
/// and a deposit.
/// </summary>
/// <remarks>
/// <para>
/// The request is checked field by field because the solver's schema is <b>strict</b>: an extra key,
/// a misspelling or a JSON number where a decimal string belongs is refused outright as
/// <c>unsupported_payload</c>, and the two profile fields are covenant parameters — quoted against a
/// value the solver never read, they produce a deposit at an address nobody watches.
/// </para>
/// <para>
/// The gates are checked because this class has no timelock. Every other corridor here has a refund
/// path to fall back on; here an unfundable quote funded anyway is a deposit reclaimable only by a
/// cooperative cancel.
/// </para>
/// </remarks>
[TestFixture]
public class ArkadeSwapRfqTests
{
    private static readonly AssetId Asset = AssetId.Create("ab".PadRight(64, 'c'), 0);

    private const string MakerPkScript =
        "5120535e2e7fb7a3fa9b3be74b13b813261497e3ad8a9d61cc45d233b4e0d21a7e73";

    private const string MakerPublicKey =
        "7c2a5ee7f0d4f5f61b0b6b1d4c9a83a0e2f5c6d7889a0b1c2d3e4f5061728394";

    private const long Now = 1_800_000_000;

    [Test]
    public void ThePair_NamesTheAssetOnTheLegThatCarriesIt()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ArkadeSwapProfile.Pair(null, Asset), Is.EqualTo($"arkade:BTC->arkade:{Asset}"));
            Assert.That(ArkadeSwapProfile.Pair(Asset, null), Is.EqualTo($"arkade:{Asset}->arkade:BTC"));
        });
    }

    [Test]
    public void APairWithNoAssetOnEitherLeg_IsRefused()
    {
        // Sats for sats is not a swap, and the solver would answer `unsupported_pair` — worth
        // saying so here, where the caller can still read which argument was empty.
        Assert.Throws<ArgumentException>(() => ArkadeSwapProfile.Pair(null, null));
    }

    [Test]
    public void TheRequest_SerializesExactlyTheFieldsTheSolverAccepts()
    {
        var request = ArkadeSwapProfile.Request(
            offerAsset: null, wantAsset: Asset, amount: 50_000, RfqAmountSide.From,
            Convert.FromHexString(MakerPkScript), Convert.FromHexString(MakerPublicKey),
            rfqId: "ab".PadRight(64, 'c'));

        var json = JsonNode.Parse(JsonSerializer.Serialize(request, RfqProtocol.Json))!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(json.Select(p => p.Key), Is.EquivalentTo(
                new[] { "v", "type", "rfq_id", "pair", "amount_side", "amount", "profile" }));
            Assert.That((int)json["v"]!, Is.EqualTo(1));
            Assert.That((string)json["type"]!, Is.EqualTo("rfq_request"));
            Assert.That((string)json["amount_side"]!, Is.EqualTo("from"));
            // A canonical decimal STRING, never a number: one leg is an asset whose atomic unit is
            // 256-bit, so the losslessness a JSON number would need cannot be checked client-side.
            Assert.That(json["amount"]!.GetValue<string>(), Is.EqualTo("50000"));
            Assert.That(json["profile"]!.AsObject().Select(p => p.Key), Is.EquivalentTo(
                new[] { "maker_pk_script", "maker_public_key" }));
            Assert.That((string)json["profile"]!["maker_pk_script"]!, Is.EqualTo(MakerPkScript));
            Assert.That((string)json["profile"]!["maker_public_key"]!, Is.EqualTo(MakerPublicKey));
        });
    }

    [Test]
    public void ARequestForATargetPayout_NamesTheToLeg()
    {
        // Exact-out is served on this corridor, unlike the EVM ones — the solver resolves the least
        // deposit whose payout reaches the asked-for amount.
        var request = ArkadeSwapProfile.Request(
            Asset, null, 100, RfqAmountSide.To,
            Convert.FromHexString(MakerPkScript), Convert.FromHexString(MakerPublicKey));

        var json = JsonNode.Parse(JsonSerializer.Serialize(request, RfqProtocol.Json))!.AsObject();

        Assert.That((string)json["amount_side"]!, Is.EqualTo("to"));
    }

    [TestCase(33)]
    [TestCase(35)]
    public void AMakerScriptThatIsNotATaprootOutput_IsRefused(int length)
    {
        // The covenant slices the witness program out of this value. A different length yields a
        // program of the wrong size and a fill obliged to pay an output nobody controls.
        Assert.Throws<ArgumentException>(() => ArkadeSwapProfile.Request(
            null, Asset, 1, RfqAmountSide.From,
            new byte[length], Convert.FromHexString(MakerPublicKey)));
    }

    [Test]
    public void AMakerKeyThatIsNotXOnly_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => ArkadeSwapProfile.Request(
            null, Asset, 1, RfqAmountSide.From,
            Convert.FromHexString(MakerPkScript), new byte[33]));
    }

    [Test]
    public void AQuote_ReadsItsAmountsAndCarrierAsAtomicStrings()
    {
        // Every amount on this class travels as a string, `carrier_sats` included.
        var quote = JsonSerializer.Deserialize<RfqQuote<ArkadeSwapQuoteProfile>>("""
            {
              "v": 1, "type": "rfq_quote", "rfq_id": "ff", "pair": "arkade:BTC->arkade:aa",
              "from_amount": "50000", "to_amount": "123456789012345678901234567890",
              "carrier_sats": "330", "solver_pubkey": "aa", "valid_until": 1800000060,
              "profile": { "offer_address": "tark1x", "offer_pk_script": "5120ab" }
            }
            """, RfqProtocol.Json)!;

        Assert.Multiple(() =>
        {
            Assert.That(quote.FromAtomicAmount, Is.EqualTo(new BigInteger(50_000)));
            Assert.That(quote.ToAtomicAmount,
                Is.EqualTo(BigInteger.Parse("123456789012345678901234567890")));
            Assert.That(quote.CarrierSats, Is.EqualTo(new BigInteger(330)));
            Assert.That(quote.Profile!.OfferAddress, Is.EqualTo("tark1x"));
        });
    }

    [Test]
    public void AQuoteWithoutACarrier_LeavesItAbsentRatherThanZero()
    {
        // Absent means "this quote prices no carrier", which is a different statement from zero —
        // and the caller needs to tell them apart to know whether to fall back to the dust floor.
        var quote = Quote();

        Assert.That(quote.CarrierSats, Is.Null);
    }

    [Test]
    public void AQuoteThatHasLapsed_IsNotFunded()
    {
        var refusal = Assert.Throws<ArkadeSwapNotFundableException>(() => ArkadeSwapGates.AssertFundable(
            Quote(validUntil: Now), Pair, 50_000, RfqAmountSide.From, Now));

        Assert.That(refusal!.Reason, Is.EqualTo(ArkadeSwapRefusal.QuoteExpired));
    }

    [Test]
    public void AQuoteForAnotherMarket_IsNotFunded()
    {
        // On a shared relay every one of a solver's replies arrives on the same subscription, so a
        // quote for the market next door is a thing that actually turns up.
        var refusal = Assert.Throws<ArkadeSwapNotFundableException>(() => ArkadeSwapGates.AssertFundable(
            Quote(pair: "arkade:BTC->arkade:ff"), Pair, 50_000, RfqAmountSide.From, Now));

        Assert.That(refusal!.Reason, Is.EqualTo(ArkadeSwapRefusal.WrongPair));
    }

    [Test]
    public void AQuoteThatRepricesTheSideWeNamed_IsNotFunded()
    {
        // The named side comes back verbatim or the quote is not an answer to this request.
        var refusal = Assert.Throws<ArkadeSwapNotFundableException>(() => ArkadeSwapGates.AssertFundable(
            Quote(from: 49_000), Pair, 50_000, RfqAmountSide.From, Now));

        Assert.That(refusal!.Reason, Is.EqualTo(ArkadeSwapRefusal.AmountRejected));
    }

    [Test]
    public void TheBounds_ApplyToTheSideTheSolverChose()
    {
        // Asserting the named side proves nothing about pricing — it is echoed. These two are the
        // only checks that bind what the solver actually decided.
        Assert.Multiple(() =>
        {
            Assert.That(() => ArkadeSwapGates.AssertFundable(
                    Quote(to: 900), Pair, 50_000, RfqAmountSide.From, Now, minToAmount: 1_000),
                Throws.TypeOf<ArkadeSwapNotFundableException>());

            Assert.That(() => ArkadeSwapGates.AssertFundable(
                    Quote(from: 60_000), Pair, 1_000, RfqAmountSide.To, Now, maxFromAmount: 55_000),
                Throws.TypeOf<ArkadeSwapNotFundableException>());
        });
    }

    [Test]
    public void AQuoteWithinItsBounds_IsFundable()
    {
        Assert.DoesNotThrow(() => ArkadeSwapGates.AssertFundable(
            Quote(), Pair, 50_000, RfqAmountSide.From, Now,
            maxFromAmount: 50_000, minToAmount: 1_000));
    }

    [Test]
    public void AnAmountTooWideForSatoshis_IsRefused_NotOverflowed()
    {
        // The wire is 256-bit and everything downstream is 64. Narrowing without asking throws
        // OverflowException, which names no field and reads as a bug here rather than as terms we
        // declined — so a caller cannot branch on it the way it branches on every other refusal.
        var tooWide = new BigInteger(long.MaxValue) + 1;

        var refusal = Assert.Throws<ArkadeSwapNotFundableException>(
            () => AssetIntentsManager.SatsOf(tooWide, "from_amount"));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Reason, Is.EqualTo(ArkadeSwapRefusal.AmountRejected));
            // The field, because three different quote fields reach this guard.
            Assert.That(refusal.Message, Does.Contain("from_amount"));
        });
    }

    [Test]
    public void AnAssetAmountTooWideForItsUnit_IsRefused()
    {
        // The one narrowing an ordinary market could reach: a whole unit of an 18-decimal asset is
        // already 10^18, so eighteen of them do not fit.
        var tooWide = new BigInteger(ulong.MaxValue) + 1;

        Assert.That(() => AssetIntentsManager.AssetUnitsOf(tooWide),
            Throws.TypeOf<ArkadeSwapNotFundableException>());
    }

    [Test]
    public void AnAmountThatFits_PassesThroughUnchanged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AssetIntentsManager.SatsOf(new BigInteger(50_000), "from_amount"), Is.EqualTo(50_000));
            Assert.That(AssetIntentsManager.AssetUnitsOf(new BigInteger(50_000)), Is.EqualTo(50_000UL));
        });
    }

    private const string Pair = "arkade:BTC->arkade:aa";

    private static RfqQuote<ArkadeSwapQuoteProfile> Quote(
        string pair = Pair, long from = 50_000, long to = 1_000, long validUntil = Now + 60) => new()
    {
        V = 1,
        Type = "rfq_quote",
        RfqId = "ff",
        Pair = pair,
        FromAtomicAmount = from,
        ToAtomicAmount = to,
        SolverPubkey = "aa",
        ValidUntil = validUntil,
    };
}
