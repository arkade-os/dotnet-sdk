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
          "base_asset": { "id": "btc", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "quote_asset": { "id": "usdt-asset-id", "name": "Tether USD", "ticker": "USDT", "decimals": 6 },
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
        Assert.That(m.QuoteAsset.Decimals, Is.EqualTo(6));
        Assert.That(m.PriceFeedSchema.PricePath, Is.EqualTo("/price"));
        Assert.That(m.PriceDecimals, Is.EqualTo(8));
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
        Assert.That(SolverDiscoveryService.NormalizePrice(100_020_000m, 8), Is.EqualTo(1.0002m));
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
    public void ComputeWantAmount_ChargesTheFlatFeeOnTopOfTheSpread()
    {
        // 1 000 000 base at price 1, 30+50 bps = 992 000, then the card's 500 flat on top.
        // Applying the spread to the remainder instead would give 991 504 — a different number from
        // the one the solver's own quote arrives at, for the same card.
        Assert.That(
            SolverDiscoveryService.ComputeWantAmount(1_000_000, 1m, feeBps: 30, feeFlat: 500),
            Is.EqualTo(991_500));
    }

    [Test]
    public void ComputeWantAmount_GivingQuote_DividesByThePrice()
    {
        // Deposit 1000 quote units at 0.5 quote-per-base: 2000 base gross, less 80 bps.
        Assert.That(
            SolverDiscoveryService.ComputeWantAmount(
                1000, 0.5m, feeBps: 30, give: MarketSide.Quote),
            Is.EqualTo(1984));
    }

    [Test]
    public void ComputeWantAmount_GivingQuote_ConvertsTheFlatFeeAndRoundsAgainstTheMaker()
    {
        // The flat fee is quote-denominated, so receiving base converts it through the price. 101
        // quote at 0.5 is 202 base exactly; 100 at 3m is 33.33… and must round UP to 34, because a
        // charge rounded down is a satoshi conceded to the maker that the solver never agreed to.
        Assert.Multiple(() =>
        {
            Assert.That(
                SolverDiscoveryService.ComputeWantAmount(
                    1000, 0.5m, feeBps: 0, safetyBps: 0, give: MarketSide.Quote, feeFlat: 101),
                Is.EqualTo(2000 - 202));
            Assert.That(
                SolverDiscoveryService.ComputeWantAmount(
                    1000, 3m, feeBps: 0, safetyBps: 0, give: MarketSide.Quote, feeFlat: 100),
                Is.EqualTo(333 - 34));
        });
    }

    [Test]
    public void ComputeWantAmount_ReturnsZeroWhenTheFlatFeeSwallowsTheTrade()
    {
        Assert.That(
            SolverDiscoveryService.ComputeWantAmount(1000, 1m, feeBps: 0, safetyBps: 0, feeFlat: 5000),
            Is.EqualTo(0));
    }

    [Test]
    public void TheInverse_HoldsAcrossPricesThatDivideBadly_AndFlatFees()
    {
        // The case decimal arithmetic loses: a price with no finite reciprocal, where rounding at
        // the 28th digit puts the two computations an atomic unit apart and the planned deposit
        // under-funds the amount it was planned for.
        decimal[] prices = [1m, 0.5m, 3m, 1.0002m, 0.0000003m, 377000.00000000m];
        long[] wants = [1, 7, 1000, 49_999, 100_000_000, 100_000_000_000_000];
        int[] fees = [0, 30, 250];
        long[] flats = [0, 1, 500];

        foreach (var give in new[] { MarketSide.Base, MarketSide.Quote })
        foreach (var price in prices)
        foreach (var want in wants)
        foreach (var fee in fees)
        foreach (var flat in flats)
        {
            long deposit;
            try
            {
                deposit = SolverDiscoveryService.ComputeRequiredDeposit(want, price, fee, give: give, feeFlat: flat);
            }
            catch (OverflowException)
            {
                // Some of these combinations have no answer an amount can hold — that is reported,
                // not rounded, and there is nothing left to round-trip.
                continue;
            }

            var got = SolverDiscoveryService.ComputeWantAmount(deposit, price, fee, give: give, feeFlat: flat);

            Assert.That(got, Is.GreaterThanOrEqualTo(want),
                $"give={give} price={price} fee={fee} flat={flat} want={want} → deposit={deposit} got={got}");
        }
    }

    [Test]
    public void ADepositNoAmountCanHold_IsRefusedRatherThanClamped()
    {
        // 10^14 units of an asset priced at 3e-7 needs ~3.3e20 sats. Clamping would quote a deposit
        // short by orders of magnitude; zero would read as free.
        Assert.Throws<OverflowException>(
            () => SolverDiscoveryService.ComputeRequiredDeposit(100_000_000_000_000, 0.0000003m, feeBps: 0));
    }

    [Test]
    public void WantingNothing_CostsNothing_EvenWithAFlatFee()
    {
        // Checked before the flat fee goes back on: otherwise asking for zero quotes a deposit worth
        // the flat fee, and this stops being the inverse of a forward that clamps zero to zero.
        Assert.That(
            SolverDiscoveryService.ComputeRequiredDeposit(0, 1m, feeBps: 30, feeFlat: 500),
            Is.EqualTo(0));
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
            // Same ids, different rail: a corridor is not the spot market it shares an id pair with.
            Market("btc", "usdt", feeBps: 1, min: 1000, max: 1_000_000, quoteCorridor: "lightning"),
        };

        var ranked = SolverDiscoveryService.FilterAndRank(markets, "btc", "usdt", baseAmount: 50_000);

        Assert.That(ranked.Select(m => m.FeeBps), Is.EqualTo(new[] { 10, 30, 50 }));
    }

    [Test]
    public void FilterAndRank_RanksByTotalFeeAtTheSize_NotBySpread()
    {
        // The flat fee is what reverses them: at 50k the 10 bps market charges 50 + 400 = 450,
        // while the 30 bps one charges 150. Ranking on the spread alone puts the dearer first.
        var markets = new[]
        {
            Market("btc", "usdt", feeBps: 10, min: 1000, max: 1_000_000, feeFlat: "400"),
            Market("btc", "usdt", feeBps: 30, min: 1000, max: 1_000_000),
        };

        var ranked = SolverDiscoveryService.FilterAndRank(markets, "btc", "usdt", baseAmount: 50_000);

        Assert.That(ranked.Select(m => m.FeeBps), Is.EqualTo(new[] { 30, 10 }));
    }

    [Test]
    public void FilterAndRank_FindsACorridorWhenOneIsAskedFor()
    {
        var markets = new[]
        {
            Market("btc", "usdt", feeBps: 30, min: 1000, max: 1_000_000),
            Market("btc", "usdt", feeBps: 90, min: 1000, max: 1_000_000, quoteCorridor: "lightning"),
        };

        var ranked = SolverDiscoveryService.FilterAndRank(
            markets, "btc", "usdt", baseAmount: 50_000, quoteCorridor: "lightning");

        Assert.That(ranked.Select(m => m.FeeBps), Is.EqualTo(new[] { 90 }));
    }

    [Test]
    public void FilterAndRank_SkipsASideTheSolverDoesNotPayOut()
    {
        var markets = new[] { Market("btc", "usdt", feeBps: 30, min: 0, max: 0) };

        Assert.That(SolverDiscoveryService.FilterAndRank(markets, "btc", "usdt", baseAmount: 50_000), Is.Empty);
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

    private static IndexedMarket Market(
        string baseId, string quoteId, int feeBps, long min, long max,
        string? quoteCorridor = null, string? feeFlat = null) => new()
    {
        Solver = "test-solver",
        Pair = $"{baseId}/{quoteId}",
        QuoteCorridor = quoteCorridor,
        FeeFlat = feeFlat,
        BaseAsset = new AssetDescriptor { Id = baseId, Decimals = 8 },
        QuoteAsset = new AssetDescriptor { Id = quoteId, Decimals = 6 },
        PriceFeed = "https://feed.example.com/price",
        PriceFeedSchema = new PriceFeedSchema { PricePath = "/price" },
        PriceDecimals = 8,
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
