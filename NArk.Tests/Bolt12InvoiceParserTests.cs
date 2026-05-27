using NArk.Swaps.Bolt12;
using NBitcoin.DataEncoders;

namespace NArk.Tests;

[TestFixture]
public class Bolt12InvoiceParserTests
{
    // A known 32-byte payment hash used across round-trip tests.
    private static readonly byte[] KnownPaymentHash =
        Convert.FromHexString("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2");

    // Builds a minimal bech32m-encoded BOLT 12 invoice that contains exactly
    // one TLV record: invoice_payment_hash (type 168, length 32).
    private static string BuildMinimalInvoice(byte[] paymentHash, string hrp = "lni")
    {
        // TLV: [0xA8][0x20][32 bytes]
        var tlv = new byte[1 + 1 + 32];
        tlv[0] = 0xA8; // type 168
        tlv[1] = 0x20; // length 32
        Array.Copy(paymentHash, 0, tlv, 2, 32);

        var encoder = Encoders.Bech32(hrp);
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        return encoder.EncodeData(tlv, Bech32EncodingType.BECH32M);
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

        var encoder = Encoders.Bech32("lni");
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        return encoder.EncodeData(tlv, Bech32EncodingType.BECH32M);
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

        var encoder = Encoders.Bech32("lni");
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        return encoder.EncodeData(tlv, Bech32EncodingType.BECH32M);
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
}
