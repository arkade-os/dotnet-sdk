using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.SolverRegistry;

namespace NArk.Tests.ArkadeIntents;

[TestFixture]
public class SolverDiscoveryServiceTests
{
    private const string SpecIndexJson = """
    {
      "version": 0,
      "network": "bitcoin",
      "generated_at": 1783958400,
      "commit": "deadbeef",
      "markets": [
        {
          "pair": "BTC/USDT",
          "solver": "arklabs-solver",
          "discovery_pubkey": "abc123",
          "base_asset": { "id": "btc", "name": "Bitcoin", "ticker": "BTC", "precision": 8 },
          "quote_asset": { "id": "usdt-asset-id", "name": "Tether USD", "ticker": "USDT", "precision": 6 },
          "price_feed": "https://feed.example.com/price?pair=BTCUSDT",
          "price_feed_schema": { "type": "json", "price_path": "/price" },
          "price_decimals": 8,
          "invert": false,
          "fee_bps": 30,
          "min_base_amount": 1000,
          "max_base_amount": 5000000
        }
      ]
    }
    """;

    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // ─── Model / parsing ──────────────────────────────────────────────

    [Test]
    public void Index_DeserializesSpecSample()
    {
        var index = JsonSerializer.Deserialize<GetSolverRegistryResponse>(SpecIndexJson, SnakeCase)!;

        Assert.That(index.Version, Is.EqualTo(0));
        Assert.That(index.Network, Is.EqualTo("bitcoin"));
        Assert.That(index.GeneratedAt, Is.EqualTo(1783958400UL));
        Assert.That(index.Markets, Has.Count.EqualTo(1));

        var m = index.Markets[0];
        Assert.That(m.Pair, Is.EqualTo("BTC/USDT"));
        Assert.That(m.Solver, Is.EqualTo("arklabs-solver"));
        Assert.That(m.DiscoveryPubkey, Is.EqualTo("abc123"));
        Assert.That(m.BaseAsset.Id, Is.EqualTo("btc"));
        Assert.That(m.QuoteAsset.Id, Is.EqualTo("usdt-asset-id"));
        Assert.That(m.QuoteAsset.Precision, Is.EqualTo(6));
        Assert.That(m.PriceFeedSchema.PricePath, Is.EqualTo("/price"));
        Assert.That(m.PriceDecimals, Is.EqualTo(8));
        Assert.That(m.Invert, Is.False);
        Assert.That(m.FeeBps, Is.EqualTo(30));
        Assert.That(m.MinBaseAmount, Is.EqualTo(1000));
        Assert.That(m.MaxBaseAmount, Is.EqualTo(5_000_000));
    }

    // ─── JSON Pointer (RFC 6901) ──────────────────────────────────────

    [Test]
    public void ResolveJsonPointer_TopLevel()
    {
        var node = JsonNode.Parse("""{ "price": 1.5 }""")!;
        Assert.That(SolverDiscoveryService.ResolveJsonPointer(node, "/price").GetValue<decimal>(), Is.EqualTo(1.5m));
    }

    [Test]
    public void ResolveJsonPointer_NestedAndArray()
    {
        var node = JsonNode.Parse("""{ "data": [ { "px": 9 } ] }""")!;
        Assert.That(SolverDiscoveryService.ResolveJsonPointer(node, "/data/0/px").GetValue<int>(), Is.EqualTo(9));
    }

    [Test]
    public void ResolveJsonPointer_UnescapesTilde()
    {
        var node = JsonNode.Parse("""{ "a/b": 3, "c~d": 4 }""")!;
        Assert.That(SolverDiscoveryService.ResolveJsonPointer(node, "/a~1b").GetValue<int>(), Is.EqualTo(3));
        Assert.That(SolverDiscoveryService.ResolveJsonPointer(node, "/c~0d").GetValue<int>(), Is.EqualTo(4));
    }

    [Test]
    public void ResolveJsonPointer_MissingMember_Throws()
    {
        var node = JsonNode.Parse("""{ "price": 1 }""")!;
        Assert.Throws<InvalidOperationException>(() => SolverDiscoveryService.ResolveJsonPointer(node, "/nope"));
    }

    // ─── Pricing ──────────────────────────────────────────────────────

    [Test]
    public void NormalizePrice_ScalesByDecimals()
    {
        Assert.That(SolverDiscoveryService.NormalizePrice(100_020_000m, 8, invert: false), Is.EqualTo(1.0002m));
    }

    [Test]
    public void NormalizePrice_Inverts()
    {
        // 250000000 / 1e8 = 2.5 → inverted = 0.4
        Assert.That(SolverDiscoveryService.NormalizePrice(250_000_000m, 8, invert: true), Is.EqualTo(0.4m));
    }

    [Test]
    public void ComputeWantAmount_MatchesFormula_WithFloor()
    {
        // floor(1_000_000 * 0.5 * (1 - (30 + 50)/10000)) = floor(1_000_000 * 0.5 * 0.992) = 496000
        Assert.That(SolverDiscoveryService.ComputeWantAmount(1_000_000, 0.5m, feeBps: 30), Is.EqualTo(496_000));
    }

    [Test]
    public void ComputeWantAmount_HonoursSafetyBps()
    {
        // No fee, no safety → exact D*P; with safety only → discounted.
        Assert.That(SolverDiscoveryService.ComputeWantAmount(1000, 2m, feeBps: 0, safetyBps: 0), Is.EqualTo(2000));
        Assert.That(SolverDiscoveryService.ComputeWantAmount(1000, 2m, feeBps: 0, safetyBps: 100), Is.EqualTo(1980));
    }

    [Test]
    public void ComputeWantAmount_LandsInsideSolverBand_ForUnitPriceMarket()
    {
        // The regtest mock market is 1 sat ↔ 1 asset unit (atomic quote-per-base price = 1).
        // Conceding only the default safety (feeBps=0) keeps the offer inside the solver's ±100 bps
        // band: floor(50000 * 1 * 0.995) = 49750 → offerPrice = feed * 1/0.995 ≈ +50 bps.
        const long deposit = 50_000;
        var want = SolverDiscoveryService.ComputeWantAmount(deposit, price: 1m, feeBps: 0);
        Assert.That(want, Is.EqualTo(49_750));

        // Solver: offerPrice = (deposit/10^8)/(want/10^0); feed (base/quote) for this market is 1e-8.
        const double feed = 1e-8;
        var offerPrice = (deposit / 1e8) / (want / 1e0);
        Assert.That(offerPrice, Is.GreaterThanOrEqualTo(feed));        // maker concedes → favours solver
        Assert.That(offerPrice, Is.LessThanOrEqualTo(feed * 1.01));    // still within +1%
    }

    [Test]
    public void ComputeRequiredDeposit_IsInverseOfComputeWantAmount()
    {
        // Naming a target want and funding the quoted deposit must yield at least that want back.
        foreach (var (want, price, fee) in new[] { (496_000L, 0.5m, 30), (2_000L, 2m, 0), (49_750L, 1m, 0) })
        {
            var deposit = SolverDiscoveryService.ComputeRequiredDeposit(want, price, fee);
            var got = SolverDiscoveryService.ComputeWantAmount(deposit, price, fee);
            Assert.That(got, Is.GreaterThanOrEqualTo(want), $"want={want} price={price} fee={fee} → deposit={deposit} got={got}");
        }
    }

    [Test]
    public void ComputeRequiredDeposit_GuardsInvalidInput()
    {
        Assert.That(SolverDiscoveryService.ComputeRequiredDeposit(0, 1m, 0), Is.EqualTo(0));
        Assert.That(SolverDiscoveryService.ComputeRequiredDeposit(1000, 0m, 0), Is.EqualTo(0));
        Assert.That(SolverDiscoveryService.ComputeRequiredDeposit(1000, 1m, feeBps: 10000), Is.EqualTo(0)); // net ≤ 0
    }

    // ─── Filter / rank ────────────────────────────────────────────────

    [Test]
    public void FilterAndRank_FiltersByPairAndBounds_OrdersByFee()
    {
        var markets = new[]
        {
            Market("btc", "usdt", feeBps: 50, min: 1000, max: 1_000_000),
            Market("btc", "usdt", feeBps: 10, min: 1000, max: 1_000_000),
            Market("btc", "usdt", feeBps: 30, min: 1000, max: 1_000_000),
            Market("btc", "eur", feeBps: 5, min: 1000, max: 1_000_000),      // wrong quote id
            Market("btc", "usdt", feeBps: 1, min: 1000, max: 5000),          // amount out of range
        };

        var ranked = SolverDiscoveryService.FilterAndRank(markets, "btc", "usdt", baseAmount: 50_000);

        Assert.That(ranked.Select(m => m.FeeBps), Is.EqualTo(new[] { 10, 30, 50 }));
    }

    // ─── HTTP: caching + price fetch ──────────────────────────────────

    [Test]
    public async Task FetchIndexAsync_CachesWithinTtl()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, SpecIndexJson));
        var svc = new SolverDiscoveryService(new HttpClient(handler));
        var url = SolverDiscoveryService.MainnetRegistry;

        var a = await svc.FetchIndexAsync(url);
        var b = await svc.FetchIndexAsync(url);

        Assert.That(a, Is.SameAs(b));
        Assert.That(handler.Calls, Is.EqualTo(1)); // second read served from cache
    }

    [Test]
    public async Task FetchPriceAsync_ExtractsAndNormalizes()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, """{ "price": 100020000 }"""));
        var svc = new SolverDiscoveryService(new HttpClient(handler));
        var market = Market("btc", "usdt", feeBps: 30, min: 0, max: long.MaxValue);

        var price = await svc.FetchPriceAsync(market);

        Assert.That(price, Is.EqualTo(1.0002m));
    }

    [Test]
    public async Task DiscoverMarketsAsync_SkipsNetworkMismatch()
    {
        // Index is for signet but we ask for bitcoin → dropped.
        var signetIndex = SpecIndexJson.Replace("\"network\": \"bitcoin\"", "\"network\": \"signet\"");
        var handler = new StubHandler(_ => (HttpStatusCode.OK, signetIndex));
        var svc = new SolverDiscoveryService(new HttpClient(handler));

        var markets = await svc.DiscoverMarketsAsync("bitcoin", registries: [SolverDiscoveryService.MainnetRegistry]);

        Assert.That(markets, Is.Empty);
    }

    [Test]
    public async Task DiscoverMarketsAsync_ReturnsMatchingMarkets()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, SpecIndexJson));
        var svc = new SolverDiscoveryService(new HttpClient(handler));

        var markets = await svc.DiscoverMarketsAsync("bitcoin", registries: [SolverDiscoveryService.MainnetRegistry]);

        Assert.That(markets, Has.Count.EqualTo(1));
        Assert.That(markets[0].Solver, Is.EqualTo("arklabs-solver"));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static IndexedMarket Market(string baseId, string quoteId, int feeBps, long min, long max) => new()
    {
        Solver = "test-solver",
        Pair = $"{baseId}/{quoteId}",
        BaseAsset = new AssetDescriptor { Id = baseId, Precision = 8 },
        QuoteAsset = new AssetDescriptor { Id = quoteId, Precision = 6 },
        PriceFeed = "https://feed.example.com/price",
        PriceFeedSchema = new PriceFeedSchema { PricePath = "/price" },
        PriceDecimals = 8,
        Invert = false,
        FeeBps = feeBps,
        MinBaseAmount = min,
        MaxBaseAmount = max,
    };

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var (code, body) = responder(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }
}
