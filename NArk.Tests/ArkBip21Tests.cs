using NArk.Abstractions.Payments;

namespace NArk.Tests;

[TestFixture]
public class ArkBip21Tests
{
    // ── Parse: BIP21 URIs ──────────────────────────────────────────────

    [Test]
    public void Parse_Bip21_WithArkAndAmount()
    {
        var info = ArkBip21.Parse("bitcoin:bc1qtest?amount=0.001&ark=tark1qtest");

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.OnchainAddress, Is.EqualTo("bc1qtest"));
        Assert.That(info.ArkAddress, Is.EqualTo("tark1qtest"));
        Assert.That(info.Amount, Is.EqualTo(0.001m));
        Assert.That(info.AmountSats, Is.EqualTo(100_000UL));
    }

    [Test]
    public void Parse_Bip21_WithAllParams()
    {
        var info = ArkBip21.Parse("bitcoin:bc1qtest?amount=0.5&ark=tark1qfoo&lightning=lnbc500u1ptest&asset=abc123");

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.OnchainAddress, Is.EqualTo("bc1qtest"));
        Assert.That(info.ArkAddress, Is.EqualTo("tark1qfoo"));
        Assert.That(info.Lightning, Is.EqualTo("lnbc500u1ptest"));
        Assert.That(info.AssetId, Is.EqualTo("abc123"));
        Assert.That(info.AmountSats, Is.EqualTo(50_000_000UL));
    }

    [Test]
    public void Parse_Bip21_ArkOnly_NoOnchainAddress()
    {
        var info = ArkBip21.Parse("bitcoin:?ark=tark1qtest");

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.OnchainAddress, Is.Null);
        Assert.That(info.ArkAddress, Is.EqualTo("tark1qtest"));
    }

    [Test]
    public void Parse_Bip21_CaseInsensitiveScheme()
    {
        var info = ArkBip21.Parse("BITCOIN:bc1qtest?ark=tark1q");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.OnchainAddress, Is.EqualTo("bc1qtest"));
    }

    [Test]
    public void Parse_Bip21_AmountRoundsCorrectly()
    {
        // 0.000000015 BTC = 1.5 sats → should round to 2
        var info = ArkBip21.Parse("bitcoin:bc1qtest?amount=0.000000015&ark=tark1q");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.AmountSats, Is.EqualTo(2UL));
    }

    [Test]
    public void Parse_Bip21_OneSat()
    {
        var info = ArkBip21.Parse("bitcoin:bc1qtest?amount=0.00000001&ark=tark1q");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.AmountSats, Is.EqualTo(1UL));
    }

    // ── Parse: Raw destinations ────────────────────────────────────────

    [Test]
    public void Parse_ArkAddress()
    {
        var info = ArkBip21.Parse("tark1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.ArkAddress, Is.EqualTo("tark1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx"));
        Assert.That(info.PreferredMethod, Is.EqualTo(ArkPaymentMethod.ArkSend));
    }

    [Test]
    public void Parse_LightningInvoice()
    {
        var info = ArkBip21.Parse("lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rq");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Lightning, Is.EqualTo("lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rq"));
        Assert.That(info.PreferredMethod, Is.EqualTo(ArkPaymentMethod.SubmarineSwap));
    }

    [Test]
    public void Parse_BitcoinAddress_Bech32()
    {
        var info = ArkBip21.Parse("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.OnchainAddress, Is.EqualTo("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"));
        Assert.That(info.PreferredMethod, Is.EqualTo(ArkPaymentMethod.ChainSwap));
    }

    [Test]
    public void Parse_BitcoinAddress_Testnet()
    {
        var info = ArkBip21.Parse("tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.OnchainAddress, Is.Not.Null);
    }

    // ── Parse: Rejection ───────────────────────────────────────────────

    [Test]
    public void Parse_Null_ReturnsNull()
    {
        Assert.That(ArkBip21.Parse(null), Is.Null);
    }

    [Test]
    public void Parse_Empty_ReturnsNull()
    {
        Assert.That(ArkBip21.Parse(""), Is.Null);
        Assert.That(ArkBip21.Parse("   "), Is.Null);
    }

    [Test]
    public void Parse_Unrecognized_ReturnsNull()
    {
        Assert.That(ArkBip21.Parse("hello world"), Is.Null);
        Assert.That(ArkBip21.Parse("main"), Is.Null);
        Assert.That(ArkBip21.Parse("3.14"), Is.Null);
        Assert.That(ArkBip21.Parse("not-an-address"), Is.Null);
    }

    [TestCase("m")]
    [TestCase("2x")]
    [TestCase("3")]
    public void Parse_TooShortForLegacyAddress_ReturnsNull(string input)
    {
        Assert.That(ArkBip21.Parse(input), Is.Null);
    }

    // ── Parse: PreferredMethod routing ──────────────────────────────────

    [Test]
    public void PreferredMethod_ArkOverLightning()
    {
        var info = ArkBip21.Parse("bitcoin:bc1qtest?ark=tark1q&lightning=lnbc1");
        Assert.That(info!.PreferredMethod, Is.EqualTo(ArkPaymentMethod.ArkSend));
    }

    [Test]
    public void PreferredMethod_AssetForcesArk()
    {
        var info = new Bip21PaymentInfo
        {
            OnchainAddress = "bc1qtest",
            AssetId = "abc123"
        };
        Assert.That(info.PreferredMethod, Is.EqualTo(ArkPaymentMethod.ArkSend));
    }

    // ── ParseStrict ────────────────────────────────────────────────────

    [Test]
    public void ParseStrict_ValidUri_Works()
    {
        var info = ArkBip21.ParseStrict("bitcoin:bc1qtest?ark=tark1q");
        Assert.That(info.ArkAddress, Is.EqualTo("tark1q"));
    }

    [Test]
    public void ParseStrict_InvalidScheme_Throws()
    {
        Assert.Throws<FormatException>(() => ArkBip21.ParseStrict("http://example.com"));
    }

    [Test]
    public void ParseStrict_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => ArkBip21.ParseStrict(""));
    }

    // ── Build ──────────────────────────────────────────────────────────

    [Test]
    public void Build_ArkOnly()
    {
        var uri = ArkBip21.Create()
            .WithArkAddress("tark1qtest")
            .Build();

        Assert.That(uri, Is.EqualTo("bitcoin:?ark=tark1qtest"));
    }

    [Test]
    public void Build_FullUri()
    {
        var uri = ArkBip21.Create()
            .WithOnchainAddress("bc1qtest")
            .WithArkAddress("tark1qtest")
            .WithAmount(0.001m)
            .WithLightning("lnbc100n1p")
            .Build();

        Assert.That(uri, Does.StartWith("bitcoin:bc1qtest?"));
        Assert.That(uri, Does.Contain("amount=0.001"));
        Assert.That(uri, Does.Contain("ark=tark1qtest"));
        Assert.That(uri, Does.Contain("lightning="));
    }

    [Test]
    public void Build_WithAsset()
    {
        var uri = ArkBip21.Create()
            .WithArkAddress("tark1qtest")
            .WithAssetId("abc123")
            .Build();

        Assert.That(uri, Does.Contain("asset=abc123"));
    }

    [Test]
    public void Build_NoAddress_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ArkBip21.Create().Build());
    }

    [Test]
    public void Build_OnchainOnly_Works()
    {
        var uri = ArkBip21.Create()
            .WithOnchainAddress("bc1qtest")
            .Build();

        Assert.That(uri, Is.EqualTo("bitcoin:bc1qtest"));
    }

    // ── Roundtrip ──────────────────────────────────────────────────────

    [Test]
    public void Build_Then_Parse_Roundtrips()
    {
        var uri = ArkBip21.Create()
            .WithOnchainAddress("bc1qtest")
            .WithArkAddress("tark1qtest")
            .WithAmount(0.005m)
            .WithLightning("lnbc500n1ptest")
            .Build();

        var info = ArkBip21.Parse(uri);

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.OnchainAddress, Is.EqualTo("bc1qtest"));
        Assert.That(info.ArkAddress, Is.EqualTo("tark1qtest"));
        Assert.That(info.Amount, Is.EqualTo(0.005m));
        Assert.That(info.AmountSats, Is.EqualTo(500_000UL));
    }
}
