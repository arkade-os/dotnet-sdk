using NArk.Swaps.Bolt12;

namespace NArk.Tests;

[TestFixture]
public class Bolt12InvoiceParserTests
{
    // A known 32-byte payment hash used across round-trip tests.
    private static readonly byte[] KnownPaymentHash =
        Convert.FromHexString("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2");

    // Builds a bech32-without-checksum BOLT 12 invoice that contains exactly
    // one TLV record: invoice_payment_hash (type 168, length 32).
    private static string BuildMinimalInvoice(byte[] paymentHash, string hrp = "lni")
    {
        // TLV: [0xA8][0x20][32 bytes]
        var tlv = new byte[1 + 1 + 32];
        tlv[0] = 0xA8; // type 168
        tlv[1] = 0x20; // length 32
        Array.Copy(paymentHash, 0, tlv, 2, 32);
        return Bolt12InvoiceParser.EncodeBolt12Bech32(hrp, tlv);
    }

    // Builds a multi-record TLV stream: offer_chains (type 0, 2 bytes),
    // invoice_created_at (type 164, 4 bytes), invoice_payment_hash (type 168, 32 bytes).
    private static string BuildMultiRecordInvoice(byte[] paymentHash)
    {
        // type 0, length 2, value 0xAB 0xCD
        // type 164, length 4, value 0x01 0x02 0x03 0x04
        // type 168, length 32, value = paymentHash
        byte[] tlv =
        [
            0x00, 0x02, 0xAB, 0xCD,
            0xA4, 0x04, 0x01, 0x02, 0x03, 0x04,
            0xA8, 0x20, .. paymentHash
        ];

        return Bolt12InvoiceParser.EncodeBolt12Bech32("lni", tlv);
    }

    // BOLT 12 does NOT use different HRP prefixes per network (unlike BOLT 11's
    // lnbc/lntb/lnbcrt). Both mainnet and testnet invoices start with lni1; the
    // chain is identified inside the TLV stream via offer_chains (type 2) which
    // contains the genesis block hash. This helper builds a realistic testnet
    // invoice TLV that includes the testnet3 genesis hash so the parser is
    // tested against data that mirrors what a real CLN/LDK testnet node emits.
    private static string BuildTestnetStyleInvoice(byte[] paymentHash)
    {
        // Testnet3 genesis hash in internal byte order (reversed from display form
        // 000000000933ea01…d77f4943).
        byte[] testnetGenesisHash = Convert.FromHexString(
            "43497fd7f826957108f4a30fd9cec3aeba79972084e90ead01ea330900000000");

        // type  2 (offer_chains):        length 32, value = testnet genesis hash
        // type 164 (invoice_created_at): length 4,  value = arbitrary timestamp
        // type 168 (invoice_payment_hash): length 32, value = paymentHash
        // type 170 (invoice_amount):     length 3,  value = 100_000 msats (tu64)
        byte[] tlv =
        [
            0x02, 0x20, .. testnetGenesisHash,
            0xA4, 0x04, 0x66, 0x48, 0xFE, 0x00,
            0xA8, 0x20, .. paymentHash,
            0xAA, 0x03, 0x01, 0x86, 0xA0,
        ];

        return Bolt12InvoiceParser.EncodeBolt12Bech32("lni", tlv);
    }

    [Test]
    public void ExtractPaymentHash_MinimalInvoice_RoundTrips()
    {
        var invoice = BuildMinimalInvoice(KnownPaymentHash);

        var result = Bolt12InvoiceParser.ExtractPaymentHash(invoice);

        Assert.That(result, Is.EqualTo(KnownPaymentHash));
    }

    [Test]
    public void ExtractPaymentHash_UpperCaseInput_Succeeds()
    {
        var invoice = BuildMinimalInvoice(KnownPaymentHash).ToUpperInvariant();

        var result = Bolt12InvoiceParser.ExtractPaymentHash(invoice);

        Assert.That(result, Is.EqualTo(KnownPaymentHash));
    }

    [Test]
    public void ExtractPaymentHash_MultipleRecords_FindsPaymentHashAmongOthers()
    {
        var invoice = BuildMultiRecordInvoice(KnownPaymentHash);

        var result = Bolt12InvoiceParser.ExtractPaymentHash(invoice);

        Assert.That(result, Is.EqualTo(KnownPaymentHash));
    }

    // ─── Network-agnostic (testnet) ───────────────────────────────────

    [Test]
    public void ExtractPaymentHash_TestnetStyleInvoice_SameHrpAsMainnet()
    {
        // Verifies that a BOLT 12 invoice carrying the testnet3 genesis hash in
        // offer_chains still uses the lni1 prefix (no lntb1 equivalent in BOLT 12).
        var invoice = BuildTestnetStyleInvoice(KnownPaymentHash);

        Assert.That(invoice, Does.StartWith("lni1"),
            "BOLT 12 invoices use lni1 regardless of network");
    }

    [Test]
    public void ExtractPaymentHash_TestnetStyleInvoice_ExtractsCorrectHash()
    {
        var invoice = BuildTestnetStyleInvoice(KnownPaymentHash);

        var result = Bolt12InvoiceParser.ExtractPaymentHash(invoice);

        Assert.That(result, Is.EqualTo(KnownPaymentHash));
    }

    [Test]
    public void IsBolt12Offer_TestnetOffer_SameHrpAsMainnet()
    {
        // Testnet BOLT 12 offers also start with lno1 — there is no lnotb1 or
        // similar variant. The chain is embedded in TLV, not the HRP.
        const string testnetOffer = "lno1qgsyxjtl6luzd9t3pr62xr7eemp6awnejusgd6gk";

        Assert.That(Bolt12InvoiceParser.IsBolt12Offer(testnetOffer), Is.True);
    }

    [Test]
    public void ExtractPaymentHash_DifferentHashes_AreDistinct()
    {
        var hash1 = new byte[32];
        var hash2 = new byte[32];
        hash2[0] = 0xFF;

        var invoice1 = BuildMinimalInvoice(hash1);
        var invoice2 = BuildMinimalInvoice(hash2);

        Assert.That(
            Bolt12InvoiceParser.ExtractPaymentHash(invoice1),
            Is.Not.EqualTo(Bolt12InvoiceParser.ExtractPaymentHash(invoice2)));
    }

    [Test]
    public void ExtractPaymentHash_Bolt11Invoice_ThrowsFormatException()
    {
        const string bolt11 = "lnbc1500n1...";

        Assert.Throws<FormatException>(() => Bolt12InvoiceParser.ExtractPaymentHash(bolt11));
    }

    [Test]
    public void ExtractPaymentHash_Bolt12Offer_ThrowsFormatException()
    {
        const string offer = "lno1qgsyxjtl6luzd9t3pr62xr7eemp6awnejusgd6gk...";

        Assert.Throws<FormatException>(() => Bolt12InvoiceParser.ExtractPaymentHash(offer));
    }

    [Test]
    public void ExtractPaymentHash_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Bolt12InvoiceParser.ExtractPaymentHash(""));
    }

    [Test]
    public void ExtractPaymentHash_NullString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Bolt12InvoiceParser.ExtractPaymentHash(null!));
    }

    [Test]
    public void IsBolt12Invoice_Lni1Prefix_ReturnsTrue()
    {
        Assert.That(Bolt12InvoiceParser.IsBolt12Invoice("lni1abc"), Is.True);
        Assert.That(Bolt12InvoiceParser.IsBolt12Invoice("LNI1ABC"), Is.True);
    }

    [Test]
    public void IsBolt12Invoice_OtherPrefixes_ReturnsFalse()
    {
        Assert.That(Bolt12InvoiceParser.IsBolt12Invoice("lno1abc"), Is.False);
        Assert.That(Bolt12InvoiceParser.IsBolt12Invoice("lnbc1abc"), Is.False);
        Assert.That(Bolt12InvoiceParser.IsBolt12Invoice(""), Is.False);
        Assert.That(Bolt12InvoiceParser.IsBolt12Invoice(null!), Is.False);
    }

    [Test]
    public void IsBolt12Offer_Lno1Prefix_ReturnsTrue()
    {
        Assert.That(Bolt12InvoiceParser.IsBolt12Offer("lno1abc"), Is.True);
        Assert.That(Bolt12InvoiceParser.IsBolt12Offer("LNO1ABC"), Is.True);
    }

    [Test]
    public void IsBolt12Offer_OtherPrefixes_ReturnsFalse()
    {
        Assert.That(Bolt12InvoiceParser.IsBolt12Offer("lni1abc"), Is.False);
        Assert.That(Bolt12InvoiceParser.IsBolt12Offer("lnbc1abc"), Is.False);
    }

    // ─── ReadBigSize unit tests ───────────────────────────────────────

    private static IEnumerable<object[]> BigSizeCases()
    {
        yield return [new byte[] { 0x00 },                   0UL,    1];
        yield return [new byte[] { 0xFC },                   252UL,  1];
        yield return [new byte[] { 0xFD, 0x00, 0xFD },       253UL,  3];
        yield return [new byte[] { 0xFD, 0xFF, 0xFF },       65535UL, 3];
        yield return [new byte[] { 0xFE, 0x00, 0x01, 0x00, 0x00 }, 65536UL, 5];
    }

    [TestCaseSource(nameof(BigSizeCases))]
    public void ReadBigSize_KnownEncodings(byte[] data, ulong expected, int expectedPos)
    {
        var pos = 0;
        var value = Bolt12InvoiceParser.ReadBigSize(data, ref pos);

        Assert.That(value, Is.EqualTo(expected));
        Assert.That(pos, Is.EqualTo(expectedPos));
    }

    [Test]
    public void FindTlvRecord_RecordPresent_ReturnsValue()
    {
        // [type=5, length=3, value=0x01 0x02 0x03]
        byte[] tlv = [0x05, 0x03, 0x01, 0x02, 0x03];

        var result = Bolt12InvoiceParser.FindTlvRecord(tlv, 5);

        Assert.That(result, Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
    }

    [Test]
    public void FindTlvRecord_RecordAbsent_ReturnsNull()
    {
        byte[] tlv = [0x05, 0x01, 0xFF];

        var result = Bolt12InvoiceParser.FindTlvRecord(tlv, 99);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void FindTlvRecord_SkipsEarlierRecords_FindsTarget()
    {
        // [type=1, length=1, 0xAA] [type=2, length=2, 0xBB 0xCC]
        byte[] tlv = [0x01, 0x01, 0xAA, 0x02, 0x02, 0xBB, 0xCC];

        var result = Bolt12InvoiceParser.FindTlvRecord(tlv, 2);

        Assert.That(result, Is.EqualTo(new byte[] { 0xBB, 0xCC }));
    }

    // ─── ExtractNodeId / ExtractOfferIssuerId / VerifyInvoiceMatchesOffer ────

    // Builds a lni1 invoice with both invoice_payment_hash (type 168) and
    // invoice_node_id (type 176). TLV records must be in ascending type order.
    private static string BuildInvoiceWithNodeId(byte[] paymentHash, byte[] nodeId)
    {
        byte[] tlv =
        [
            0xA8, 0x20, .. paymentHash, // type 168, length 32
            0xB0, 0x21, .. nodeId,      // type 176, length 33
        ];
        return Bolt12InvoiceParser.EncodeBolt12Bech32("lni", tlv);
    }

    private static readonly byte[] KnownNodeId =
        Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619");

    private static readonly byte[] OtherNodeId =
        Convert.FromHexString("0303030303030303030303030303030303030303030303030303030303030303ab");

    [Test]
    public void ExtractNodeId_InvoiceWithNodeId_ReturnsCorrectKey()
    {
        var invoice = BuildInvoiceWithNodeId(KnownPaymentHash, KnownNodeId);

        var result = Bolt12InvoiceParser.ExtractNodeId(invoice);

        Assert.That(result, Is.EqualTo(KnownNodeId));
    }

    [Test]
    public void ExtractNodeId_InvoiceWithoutNodeId_ThrowsFormatException()
    {
        var invoice = BuildMinimalInvoice(KnownPaymentHash); // only type 168, no type 176

        Assert.Throws<FormatException>(() => Bolt12InvoiceParser.ExtractNodeId(invoice));
    }

    [Test]
    public void ExtractOfferIssuerId_MinimalOffer_ReturnsKnownKey()
    {
        var result = Bolt12InvoiceParser.ExtractOfferIssuerId(MinimalOffer);

        Assert.That(result, Is.Not.Null);
        Assert.That(Convert.ToHexString(result!).ToLowerInvariant(),
            Is.EqualTo(MinimalOfferIssuerIdHex));
    }

    [Test]
    public void ExtractOfferIssuerId_BlindedPathOnlyOffer_ReturnsNull()
    {
        // "no issuer_id and blinded path" from offers-test.json — no TLV type 22.
        const string blindedPathOffer =
            "lno1pgx9getnwss8vetrw3hhyucs5ypjgef743p5fzqq9nqxh0ah7y87rzv3ud0eleps9kl2d5348hq2k8qzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgqpqqqqqqqqqqqqqqqqqqqqqqqqqqqzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqzq3zyg3zyg3zygszqqqqyqqqqsqqvpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqsq";

        var result = Bolt12InvoiceParser.ExtractOfferIssuerId(blindedPathOffer);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void VerifyInvoiceMatchesOffer_MatchingKeys_DoesNotThrow()
    {
        // invoice_node_id == offer_issuer_id (both = KnownNodeId / MinimalOffer key).
        var invoice = BuildInvoiceWithNodeId(KnownPaymentHash, KnownNodeId);

        Assert.DoesNotThrow(() =>
            Bolt12InvoiceParser.VerifyInvoiceMatchesOffer(invoice, MinimalOffer));
    }

    [Test]
    public void VerifyInvoiceMatchesOffer_MismatchedKeys_ThrowsInvalidOperationException()
    {
        var invoice = BuildInvoiceWithNodeId(KnownPaymentHash, OtherNodeId);

        Assert.Throws<InvalidOperationException>(() =>
            Bolt12InvoiceParser.VerifyInvoiceMatchesOffer(invoice, MinimalOffer));
    }

    [Test]
    public void VerifyInvoiceMatchesOffer_BlindedPathOnlyOffer_SkipsVerification()
    {
        // Offer has no offer_issuer_id — verification is skipped, any invoice passes.
        const string blindedPathOffer =
            "lno1pgx9getnwss8vetrw3hhyucs5ypjgef743p5fzqq9nqxh0ah7y87rzv3ud0eleps9kl2d5348hq2k8qzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgqpqqqqqqqqqqqqqqqqqqqqqqqqqqqzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqzq3zyg3zyg3zygszqqqqyqqqqsqqvpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqsq";
        var invoice = BuildInvoiceWithNodeId(KnownPaymentHash, OtherNodeId);

        Assert.DoesNotThrow(() =>
            Bolt12InvoiceParser.VerifyInvoiceMatchesOffer(invoice, blindedPathOffer));
    }

    // ─── Official BOLT 12 test vectors (lightning/bolts bolt12/) ─────────────
    // Source: https://github.com/lightning/bolts/blob/master/bolt12/offers-test.json
    // Offer strings are bech32-without-checksum encoded; field values are confirmed
    // by the spec test vector JSON (type, length, hex).

    // Minimal offer: single TLV type=22 (offer_issuer_id), length=33.
    private const string MinimalOffer =
        "lno1zcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pksese";

    private const string MinimalOfferIssuerIdHex =
        "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619";

    // Testnet offer: TLV type=2 (offer_chains) with testnet3 genesis hash, then
    // type=10 (offer_description), then type=22 (offer_issuer_id).
    private const string TestnetOffer =
        "lno1qgsyxjtl6luzd9t3pr62xr7eemp6awnejusgf6gw45q75vcfqqqqqqq2p32x2um5ypmx2cm5dae8x93pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpj";

    private const string Testnet3GenesisHashHex =
        "43497fd7f826957108f4a30fd9cec3aeba79972084e90ead01ea330900000000";

    // Offer with description ("Test vectors") and issuer_id.
    private const string OfferWithDescription =
        "lno1pgx9getnwss8vetrw3hhyuckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg";

    // Invalid: bech32 padding exceeds 4-bit limit — decoder must reject this.
    private const string InvalidPaddingOffer =
        "lno1zcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pkseseq";

    [Test]
    public void DecodeBolt12Bech32_MinimalOffer_FindsIssuerIdTlv()
    {
        var tlv = Bolt12InvoiceParser.DecodeBolt12Bech32(MinimalOffer);

        var issuerId = Bolt12InvoiceParser.FindTlvRecord(tlv, 22); // offer_issuer_id

        Assert.That(issuerId, Is.Not.Null);
        Assert.That(Convert.ToHexString(issuerId!).ToLowerInvariant(),
            Is.EqualTo(MinimalOfferIssuerIdHex));
    }

    [Test]
    public void DecodeBolt12Bech32_TestnetOffer_FindsChainsTlv()
    {
        var tlv = Bolt12InvoiceParser.DecodeBolt12Bech32(TestnetOffer.ToLowerInvariant());

        var chains = Bolt12InvoiceParser.FindTlvRecord(tlv, 2); // offer_chains

        Assert.That(chains, Is.Not.Null);
        Assert.That(Convert.ToHexString(chains!).ToLowerInvariant(),
            Is.EqualTo(Testnet3GenesisHashHex));
    }

    [Test]
    public void DecodeBolt12Bech32_OfferWithDescription_FindsDescriptionTlv()
    {
        var tlv = Bolt12InvoiceParser.DecodeBolt12Bech32(OfferWithDescription);

        var description = Bolt12InvoiceParser.FindTlvRecord(tlv, 10); // offer_description

        Assert.That(description, Is.Not.Null);
        // "Test vectors" in UTF-8
        Assert.That(System.Text.Encoding.UTF8.GetString(description!), Is.EqualTo("Test vectors"));
    }

    // All 20 valid offers from offers-test.json (SetName matches the "description" field).
    private static IEnumerable<TestCaseData> ValidOffers()
    {
        yield return new TestCaseData(
            "lno1zcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pksese")
            .SetName("minimal");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyuckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg")
            .SetName("with_description");
        yield return new TestCaseData(
            "lno1qgsyxjtl6luzd9t3pr62xr7eemp6awnejusgf6gw45q75vcfqqqqqqq2p32x2um5ypmx2cm5dae8x93pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpj")
            .SetName("for_testnet");
        yield return new TestCaseData(
            "lno1qgsxlc5vp2m0rvmjcxn2y34wv0m5lyc7sdj7zksgn35dvxgqqqqqqqq2p32x2um5ypmx2cm5dae8x93pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpj")
            .SetName("for_bitcoin_redundant");
        yield return new TestCaseData(
            "lno1qfqpge38tqmzyrdjj3x2qkdr5y80dlfw56ztq6yd9sme995g3gsxqqm0u2xq4dh3kdevrf4zg6hx8a60jv0gxe0ptgyfc6xkryqqqqqqqq9qc4r9wd6zqan9vd6x7unnzcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pksese")
            .SetName("for_bitcoin_or_liquidv1");
        yield return new TestCaseData(
            "lno1qsgqqqqqqqqqqqqqqqqqqqqqqqqqqzsv23jhxapqwejkxar0wfe3vggzamrjghtt05kvkvpcp0a79gmy3nt6jsn98ad2xs8de6sl9qmgvcvs")
            .SetName("with_metadata");
        yield return new TestCaseData(
            "lno1pqpzwyq2p32x2um5ypmx2cm5dae8x93pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpj")
            .SetName("with_amount");
        yield return new TestCaseData(
            "lno1qcp4256ypqpzwyq2p32x2um5ypmx2cm5dae8x93pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpj")
            .SetName("with_currency");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyucwq3ay997czcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pksese")
            .SetName("with_expiry");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyucjy358garswvaz7tmzdak8gvfj9ehhyeeqgf85c4p3xgsxjmnyw4ehgunfv4e3vggzamrjghtt05kvkvpcp0a79gmy3nt6jsn98ad2xs8de6sl9qmgvcvs")
            .SetName("with_issuer");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyuc5qyz3vggzamrjghtt05kvkvpcp0a79gmy3nt6jsn98ad2xs8de6sl9qmgvcvs")
            .SetName("with_quantity");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyuc5qqtzzqhwcuj966ma9n9nqwqtl032xeyv6755yeflt235pmww58egx6rxry")
            .SetName("unlimited_quantity");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyuc5qyq3vggzamrjghtt05kvkvpcp0a79gmy3nt6jsn98ad2xs8de6sl9qmgvcvs")
            .SetName("single_quantity");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyucvp5yqqqqqqqqqqqqqqqqqqqqkyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg")
            .SetName("with_feature_bit_99");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyucs5ypjgef743p5fzqq9nqxh0ah7y87rzv3ud0eleps9kl2d5348hq2k8qzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgqpqqqqqqqqqqqqqqqqqqqqqqqqqqqzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqzq3zyg3zyg3zyg3vggzamrjghtt05kvkvpcp0a79gmy3nt6jsn98ad2xs8de6sl9qmgvcvs")
            .SetName("blinded_path_via_bob");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyucs3yqqqqqqqqqqqqp2qgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqyqqqqqqqqqqqqqqqqqqqqqqqqqqqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqqgzyg3zyg3zyg3z93pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpj")
            .SetName("blinded_path_sciddir");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyucs5ypjgef743p5fzqq9nqxh0ah7y87rzv3ud0eleps9kl2d5348hq2k8qzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgqpqqqqqqqqqqqqqqqqqqqqqqqqqqqzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqzq3zyg3zyg3zygszqqqqyqqqqsqqvpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqsq")
            .SetName("no_issuer_id_blinded_path");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyucsl5qj5qeyv5l2cs6y3qqzesrth7mlzrlp3xg7xhulusczm04x6g6nms9trspqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqqsqqqqqqqqqqqqqqqqqqqqqqqqqqpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqsqpqg3zyg3zyg3zygpqqqqzqqqqgqqxqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqqgqqqqqqqqqqqqqqqqqqqqqqqqqqqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgqqsg3zyg3zyg3zygtzzqhwcuj966ma9n9nqwqtl032xeyv6755yeflt235pmww58egx6rxry")
            .SetName("two_blinded_paths");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyuckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxfppf5x2mrvdamk7unvvs")
            .SetName("unknown_odd_field");
        yield return new TestCaseData(
            "lno1pgx9getnwss8vetrw3hhyuckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvx078wdv5gg2dpjkcmr0wahhymry")
            .SetName("unknown_odd_experimental_field");
    }

    [TestCaseSource(nameof(ValidOffers))]
    public void IsBolt12Offer_ValidOfferTestVectors_ReturnsTrue(string offer)
    {
        Assert.That(Bolt12InvoiceParser.IsBolt12Offer(offer), Is.True);
    }

    [TestCaseSource(nameof(ValidOffers))]
    public void DecodeBolt12Bech32_ValidOfferTestVectors_DoesNotThrow(string offer)
    {
        Assert.DoesNotThrow(() => Bolt12InvoiceParser.DecodeBolt12Bech32(offer));
    }

    // Invalid offers from offers-test.json where the bech32 encoding itself is wrong
    // (not just semantic BOLT 12 violations, which our minimal parser doesn't enforce).
    private static IEnumerable<TestCaseData> InvalidEncodingOffers()
    {
        yield return new TestCaseData(
            "lno1zcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pkseseq")
            .SetName("padding_exceeds_4bit_limit");
        yield return new TestCaseData("lno1")
            .SetName("empty_data_part");
    }

    [TestCaseSource(nameof(InvalidEncodingOffers))]
    public void DecodeBolt12Bech32_InvalidEncoding_ThrowsFormatException(string offer)
    {
        Assert.Throws<FormatException>(
            () => Bolt12InvoiceParser.DecodeBolt12Bech32(offer));
    }

    // format-string-test.json: uppercase variants must decode to the same bytes.
    [Test]
    public void DecodeBolt12Bech32_UppercaseFormatString_DecodesIdentically()
    {
        const string lower = "lno1pqps7sjqpgtyzm3qv4uxzmtsd3jjqer9wd3hy6tsw35k7msjzfpy7nz5yqcnygrfdej82um5wf5k2uckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg";
        const string upper = "LNO1PQPS7SJQPGTYZM3QV4UXZMTSD3JJQER9WD3HY6TSW35K7MSJZFPY7NZ5YQCNYGRFDEJ82UM5WF5K2UCKYYPWA3EYT44H6TXTXQUQH7LZ5DJGE4AFGFJN7K4RGRKUAG0JSD5XVXG";

        var fromLower = Bolt12InvoiceParser.DecodeBolt12Bech32(lower);
        var fromUpper = Bolt12InvoiceParser.DecodeBolt12Bech32(upper.ToLowerInvariant());

        Assert.That(fromLower, Is.EqualTo(fromUpper));
    }

}
