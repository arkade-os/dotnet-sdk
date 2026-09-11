using System.Text.Json;
using System.Text.Json.Nodes;
using System.Numerics;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.SolverRegistry;

namespace NArk.Tests.ArkadeIntents.Rfq;

public class CurrentWireContractTests
{
    private const string Amount = "115792089237316195423570985008687907853269984665640564039457584007913129639935";

    [Test]
    public void Quote_RoundTripsFullWidthAtomicAmounts()
    {
        var json = $$"""{"from_amount":"{{Amount}}","to_amount":"10","solver_pubkey":"key"}""";
        var quote = JsonSerializer.Deserialize<RfqQuote<JsonObject>>(json, RfqProtocol.Json)!;
        var wire = JsonSerializer.SerializeToNode(quote, RfqProtocol.Json)!;
        Assert.That(wire["from_amount"]!.GetValue<string>(), Is.EqualTo(Amount));
        Assert.That(() => quote.FromAmount, Throws.TypeOf<OverflowException>());
    }

    [Test]
    public void Request_RoundTripsFullWidthAtomicAmounts()
    {
        var json = $$$"""{"rfq_id":"id","pair":"pair","amount_side":"from","amount":"{{{Amount}}}","profile":{}}""";
        var request = JsonSerializer.Deserialize<RfqRequest<JsonObject>>(json, RfqProtocol.Json)!;
        Assert.That(JsonSerializer.SerializeToNode(request, RfqProtocol.Json)!["amount"]!.GetValue<string>(), Is.EqualTo(Amount));
    }

    [TestCase("-1")]
    [TestCase("-9223372036854775808")]
    public void Quote_RejectsNegativeNumericAmounts(string amount)
    {
        Assert.That(() => JsonSerializer.Deserialize<RfqQuote<JsonObject>>(
            $$"""{"from_amount":{{amount}},"to_amount":1,"solver_pubkey":"key"}""", RfqProtocol.Json),
            Throws.TypeOf<JsonException>());
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task Discovery_AcceptsCurrentCardsWithoutDisplayPair(int version)
    {
        var json = $$"""
            {"version":{{version}},"name":"solver","markets":[{
              "base_asset":{"id":"arkade:regtest/slip44:1","decimals":8},
              "quote_asset":{"id":"bolt11:regtest/slip44:1","decimals":8},
              "min_base_amount":"1","max_base_amount":"1000",
              "min_quote_amount":"1","max_quote_amount":"1000"}]}
            """;
        var card = JsonSerializer.Deserialize<SolverCard>(json, RfqProtocol.Json)!;
        var markets = await new SolverDiscoveryService(new HttpClient()).DiscoverMarketsAsync("regtest", [], [card]);
        Assert.That(markets, Has.Count.EqualTo(1));
        Assert.That(markets[0].LegKey(MarketSide.Quote), Is.EqualTo("bolt11:regtest/slip44:1"));
        Assert.That(markets[0].IsSameAsset, Is.True);
        Assert.That(markets[0].IsCorridor, Is.True);
    }

    [Test]
    public void Discovery_PrefersCanonicalIdentityOverLegacyIndexProjection()
    {
        var market = JsonSerializer.Deserialize<SolverMarket>("""
            {"pair":"BTC/BTC","base_asset":{"id":"btc","caip19_id":"arkade:regtest/slip44:1"},
             "quote_asset":{"id":"btc","caip19_id":"bolt11:regtest/slip44:1"},"quote_corridor":"lightning"}
            """, RfqProtocol.Json)!;
        Assert.That(market.PairKey(), Is.EqualTo("arkade:regtest/slip44:1/bolt11:regtest/slip44:1"));
    }

    [Test]
    public void Registry_RoundTripsFullWidthBounds()
    {
        var market = JsonSerializer.Deserialize<SolverMarket>($$"""
            {"pair":"BTC/ETH","base_asset":{"id":"arkade:regtest/slip44:1"},
             "quote_asset":{"id":"eip155:31337/slip44:60"},"max_quote_amount":"{{Amount}}"}
            """, RfqProtocol.Json)!;
        Assert.That(JsonSerializer.SerializeToNode(market, RfqProtocol.Json)!["max_quote_amount"]!.GetValue<string>(), Is.EqualTo(Amount));
    }

    [Test]
    public void Quote_RejectsAnotherEnvelopeVersion()
    {
        Assert.That(() => RfqProtocol.ExpectQuote<JsonObject>(JsonNode.Parse("""
            {"v":2,"type":"rfq_quote","rfq_id":"id","solver_pubkey":"key"}
            """)!, "id"), Throws.InvalidOperationException);
    }

    [Test]
    public void CanonicalMarket_MatchesLegacySolverPairWithoutUsingTheDisplayLabel()
    {
        var card = JsonSerializer.Deserialize<SolverCard>("""
            {"version":0,"name":"solver","markets":[{
              "base_asset":{"id":"arkade:regtest/slip44:1"},
              "quote_asset":{"id":"bolt11:regtest/slip44:1"},
              "min_quote_amount":"10","max_quote_amount":"100"}]}
            """, RfqProtocol.Json)!;
        Assert.That(SolverTerms.MarketFor(card, "arkade:BTC->lightning:BTC"), Is.Not.Null);
        Assert.That(() => SolverTerms.AssertWithinLimits(card, "arkade:BTC->lightning:BTC", 9),
            Throws.TypeOf<SolverTermsException>());
    }

    [Test]
    public void CanonicalAdapter_DoesNotInferAnExternalTokenTicker()
    {
        Assert.That(LegacyRfqPairAdapter.FromCanonical(AssetIdentifier.Parse("arkade:regtest/slip44:1"),
            AssetIdentifier.Parse("bolt11:regtest/slip44:1")), Is.EqualTo("arkade:BTC->lightning:BTC"));
        Assert.That(() => LegacyRfqPairAdapter.FromCanonical(AssetIdentifier.Parse("arkade:regtest/slip44:1"),
            AssetIdentifier.Parse("eip155:31337/slip44:60")), Throws.ArgumentException);
    }

    [Test]
    public void StatusRefusal_PreservesStructuredDiagnostics()
    {
        var error = Assert.Throws<RfqRefusedException>(() => RfqProtocol.ReadStatus<JsonObject>(JsonNode.Parse("""
            {"v":1,"type":"rfq_refusal","rfq_id":"id","reason":"unsupported_payload",
             "error_code":"invoice_cltv_too_large","field":"profile.invoice","actual":624,"limit":288,"unit":"blocks"}
            """)!, "id"));
        Assert.That(error!.Refusal.ErrorCode, Is.EqualTo("invoice_cltv_too_large"));
        Assert.That(error.Refusal.Actual, Is.EqualTo(624));
        Assert.That(error.Refusal.Limit, Is.EqualTo(288));
    }

    [Test]
    public void Status_RejectsAnotherNegotiation()
    {
        Assert.That(() => RfqProtocol.ReadStatus<JsonObject>(JsonNode.Parse("""
            {"v":1,"type":"rfq_status","rfq_id":"other","state":"settled"}
            """)!, "id"), Throws.InvalidOperationException);
    }

    [Test]
    public void CurrentOnchainRangeAndFundingFields_RoundTripAtTheirWireLocations()
    {
        var quote = JsonSerializer.Deserialize<RfqQuote<JsonObject>>("""
            {"solver_pubkey":"key","from_amount":1000,"to_amount":900,"min_from_amount":800,"max_from_amount":1200}
            """, RfqProtocol.Json)!;
        Assert.That(quote.MinFromAmount!.Value.ToString(), Is.EqualTo("800"));
        Assert.That(quote.MaxFromAmount!.Value.ToString(), Is.EqualTo("1200"));
        var status = RfqProtocol.ReadStatus<NArk.ArkadeIntents.Rfq.Profiles.Onchain.OnchainReceiveStatusProfile>(JsonNode.Parse("""
            {"v":1,"type":"rfq_status","rfq_id":"id","state":"funded","profile":{"funded_from_amount":1100,"funded_to_amount":1000}}
            """)!, "id")!;
        var wire = JsonSerializer.SerializeToNode(status, RfqProtocol.Json)!;
        Assert.That(wire["profile"]!["funded_from_amount"]!.GetValue<long>(), Is.EqualTo(1100));
        Assert.That(wire["profile"]!["funded_to_amount"]!.GetValue<long>(), Is.EqualTo(1000));
    }

    [Test]
    public async Task Discovery_RejectsACanonicalCardForAnotherNetwork()
    {
        var card = JsonSerializer.Deserialize<SolverCard>("""
            {"version":0,"name":"solver","markets":[{
              "base_asset":{"id":"arkade:bitcoin/slip44:0"},
              "quote_asset":{"id":"bolt11:bitcoin/slip44:0"}}]}
            """, RfqProtocol.Json)!;
        var markets = await new SolverDiscoveryService(new HttpClient()).DiscoverMarketsAsync("regtest", [], [card]);
        Assert.That(markets, Is.Empty);
    }

    [Test]
    public async Task Discovery_PreservesEvmChainIdentityFromVersionOneCards()
    {
        var card = JsonSerializer.Deserialize<SolverCard>($$"""
            {"version":1,"name":"solver","markets":[{
              "base_asset":{"id":"arkade:regtest/slip44:1"},
              "quote_asset":{"id":"eip155:31337/slip44:60"},"max_quote_amount":"{{Amount}}"}]}
            """, RfqProtocol.Json)!;
        var markets = await new SolverDiscoveryService(new HttpClient()).DiscoverMarketsAsync("regtest", [], [card]);
        Assert.That(markets, Has.Count.EqualTo(1));
        Assert.That(markets[0].LegKey(MarketSide.Quote), Is.EqualTo("eip155:31337/slip44:60"));
        Assert.That(markets[0].MaxQuoteAtomicAmount.ToString(), Is.EqualTo(Amount));
    }

    [Test]
    public void ReceiveProfile_AcceptsOmittedOptionalClaimPacket()
    {
        var profile = JsonSerializer.Deserialize<NArk.ArkadeIntents.Rfq.Profiles.Lightning.LightningReceiveRequestProfile>("""
            {"payment_hash":"hash","payout_address":"address","payout_pubkey":"key"}
            """, RfqProtocol.Json)!;
        Assert.That(profile.ClaimPacket, Is.Null);
        var onchain = JsonSerializer.Deserialize<NArk.ArkadeIntents.Rfq.Profiles.Onchain.OnchainReceiveRequestProfile>("""
            {"payment_hash":"hash","payout_address":"address","payout_pubkey":"key","refund_pubkey":"key"}
            """, RfqProtocol.Json)!;
        Assert.That(onchain.ClaimPacket, Is.Null);
    }

    [TestCase("-1")]
    [TestCase("-9223372036854775808")]
    public void LegacyAmountConverter_RejectsNegativeNumbers(string amount)
    {
        var options = new JsonSerializerOptions { Converters = { new NArk.ArkadeIntents.Rfq.Converters.RfqAmountConverter() } };
        Assert.That(() => JsonSerializer.Deserialize<long>(amount, options), Throws.TypeOf<JsonException>());
    }

    [Test]
    public async Task Discovery_DoesNotAdmitExternalAssetsFromVersionZeroCards()
    {
        var card = JsonSerializer.Deserialize<SolverCard>("""
            {"version":0,"name":"solver","markets":[{
              "base_asset":{"id":"arkade:regtest/slip44:1"},
              "quote_asset":{"id":"eip155:31337/slip44:60"}}]}
            """, RfqProtocol.Json)!;
        var markets = await new SolverDiscoveryService(new HttpClient()).DiscoverMarketsAsync("regtest", [], [card]);
        Assert.That(markets, Is.Empty);
    }

    [Test]
    public void LegacyAmountConverter_RejectsNegativeWrites()
    {
        var options = new JsonSerializerOptions { Converters = { new NArk.ArkadeIntents.Rfq.Converters.RfqAmountConverter() } };
        Assert.That(() => JsonSerializer.Serialize(-1L, options), Throws.TypeOf<JsonException>());
    }

    [TestCase("btc")]
    [TestCase("eip155:01/slip44:60")]
    [TestCase("eip155:1/erc20:0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [TestCase("arkade:bitcoin/slip44:1")]
    [TestCase("bolt11:regtest/slip44:0")]
    public void CanonicalIdentity_RejectsAmbiguousOrNonCanonicalSpellings(string id)
    {
        Assert.That(() => AssetIdentifier.Parse(id), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void FullWidthFeeComparison_DoesNotNarrowTheFlatFeeInItsDiagnostic()
    {
        var card = JsonSerializer.Deserialize<SolverCard>($$"""
            {"version":1,"name":"solver","markets":[{
              "base_asset":{"id":"eip155:1/slip44:60"},"quote_asset":{"id":"eip155:1/slip44:60"},
              "fee_bps":0,"fee_flat":"{{Amount}}"}]}
            """, RfqProtocol.Json)!;
        var quote = new RfqQuote<JsonObject>
        {
            Pair = "eip155:1/slip44:60->eip155:1/slip44:60", SolverPubkey = "key",
            FromAtomicAmount = BigInteger.Parse(Amount) + 2, ToAtomicAmount = 0,
        };
        Assert.That(() => SolverTerms.AssertFeeWithinAdvertised(card, quote), Throws.TypeOf<SolverTermsException>());
    }

    [Test]
    public void Registry_RoundTripsFullWidthFlatFees()
    {
        var market = new SolverMarket
        {
            BaseAsset = new AssetDescriptor { Id = "eip155:1/slip44:60" },
            QuoteAsset = new AssetDescriptor { Id = "eip155:2/slip44:60" }, FeeFlat = Amount,
        };
        Assert.That(JsonSerializer.SerializeToNode(market, RfqProtocol.Json)!["fee_flat"]!.GetValue<string>(), Is.EqualTo(Amount));
    }

    [Test]
    public void LegacyTermsMatching_DoesNotDiscardConflictingNetworks()
    {
        var card = new SolverCard
        {
            Name = "solver", Markets = [new SolverMarket
            {
                BaseAsset = new AssetDescriptor { Id = "arkade:bitcoin/slip44:0" },
                QuoteAsset = new AssetDescriptor { Id = "bolt11:regtest/slip44:1" },
            }],
        };
        Assert.That(SolverTerms.MarketFor(card, "arkade:BTC->lightning:BTC"), Is.Null);
    }

    [TestCase(null)]
    [TestCase(Amount)]
    public void NullableAtomicConverters_RoundTripNullAndFullWidthValues(string? amount)
    {
        var raw = amount is null ? "null" : JsonSerializer.Serialize(amount);
        var request = JsonSerializer.Deserialize<RfqRequest<JsonObject>>($$"""
            {"rfq_id":"id","pair":"pair","amount_side":"from","profile":{},
             "amount":{{raw}},"min_from_amount":{{raw}},"max_from_amount":{{raw}}}
            """, RfqProtocol.Json)!;
        var quote = JsonSerializer.Deserialize<RfqQuote<JsonObject>>($$"""
            {"solver_pubkey":"key","min_from_amount":{{raw}},"max_from_amount":{{raw}}}
            """, RfqProtocol.Json)!;
        BigInteger? expected = amount is null ? null : BigInteger.Parse(amount);
        Assert.That(request.AtomicAmount, Is.EqualTo(expected));
        Assert.That(request.MinFromAmount, Is.EqualTo(expected));
        Assert.That(request.MaxFromAmount, Is.EqualTo(expected));
        Assert.That(quote.MinFromAmount, Is.EqualTo(expected));
        Assert.That(quote.MaxFromAmount, Is.EqualTo(expected));
        var requestWire = JsonSerializer.SerializeToNode(request, RfqProtocol.Json)!;
        var quoteWire = JsonSerializer.SerializeToNode(quote, RfqProtocol.Json)!;
        foreach (var field in new[] { "amount", "min_from_amount", "max_from_amount" })
            Assert.That(requestWire[field]?.GetValue<string>(), Is.EqualTo(amount));
        foreach (var field in new[] { "min_from_amount", "max_from_amount" })
            Assert.That(quoteWire[field]?.GetValue<string>(), Is.EqualTo(amount));
        if (amount is null)
        {
            Assert.That(requestWire.AsObject().ContainsKey("amount"), Is.False);
            Assert.That(quoteWire.AsObject().ContainsKey("min_from_amount"), Is.False);
        }
    }

    [Test]
    public async Task Discovery_AdmitsPublishedVersionZeroIndexWithCanonicalProjection()
    {
        const string json = """
            {"version":0,"network":"regtest","generated_at":1,"markets":[{
              "solver":"solver","pair":"BTC/lightning:BTC",
              "base_asset":{"id":"btc","caip19_id":"arkade:regtest/slip44:1"},
              "quote_asset":{"id":"btc","caip19_id":"bolt11:regtest/slip44:1"},
              "quote_corridor":"lightning","min_base_amount":"1","max_base_amount":"1000"}]}
            """;
        using var http = new HttpClient(new IndexHandler(json));
        var markets = await new SolverDiscoveryService(http).DiscoverMarketsAsync("regtest",
            [new Uri("https://registry.test/regtest.json")]);
        var ranked = SolverDiscoveryService.FilterAndRank(markets,
            "arkade:regtest/slip44:1", "bolt11:regtest/slip44:1", 50);
        Assert.That(ranked, Has.Count.EqualTo(1));
        Assert.That(ranked[0].BaseAsset.Id, Is.EqualTo("btc"));
        Assert.That(ranked[0].IsSameAsset, Is.True);
    }

    private sealed class IndexHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
