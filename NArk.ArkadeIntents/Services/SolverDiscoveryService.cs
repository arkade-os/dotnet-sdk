using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NArk.ArkadeIntents.SolverRegistry;

namespace NArk.ArkadeIntents.Services;

/// <summary>
/// Client for the Arkade Market Discovery Protocol v0: fetches per-network solver indexes, merges
/// them with local cards, filters/ranks markets for a trade, reads the market's price feed and
/// derives the maker's <c>wantAmount</c>.
/// </summary>
/// <remarks>
/// The trust anchor is each registry the client follows (PR review is the listing gate, git history
/// the audit log, HTTPS the transport integrity); clients may follow several registries and add
/// local cards. Indexes are cached for <see cref="_cacheTtl"/> (spec TTL ~10 min). The dormant v1
/// (signed quotes over Nostr) is intentionally not implemented.
/// </remarks>
public sealed class SolverDiscoveryService
{
    /// <summary>Default per-network index URLs published by the reference registry.</summary>
    // todo(15.06.2026): i don't like it being here. we probably should move it to ArkNetworkConfig or smh
    public static readonly Uri MainnetRegistry = new("https://arkade-os.github.io/solver-registry/bitcoin.json");
    public static readonly Uri SignetRegistry = new("https://arkade-os.github.io/solver-registry/signet.json");
    public static readonly Uri MutinynetRegistry = new("https://arkade-os.github.io/solver-registry/mutinynet.json");
    public static readonly Uri RegtestRegistry = new("https://arkade-os.github.io/solver-registry/regtest.json");

    /// <summary>The only discovery protocol version this client understands.</summary>
    public const int SupportedVersion = 0;

    /// <summary>Suggested default client-side safety cushion, in basis points.</summary>
    public const int DefaultSafetyBps = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        // The registry encodes amounts (min/max base & quote) as JSON strings, e.g. "min_base_amount": "1000".
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _stalenessThreshold;
    private readonly ILogger<SolverDiscoveryService>? _logger;
    private readonly Dictionary<Uri, (DateTimeOffset FetchedAt, GetSolverRegistryResponse Index)> _cache = new();
    private readonly object _cacheLock = new();

    public SolverDiscoveryService(HttpClient http, ILogger<SolverDiscoveryService>? logger = null)
        : this(http, TimeSpan.FromMinutes(10), TimeSpan.FromDays(7), logger)
    {
    }

    public SolverDiscoveryService(
        HttpClient http,
        TimeSpan cacheTtl,
        TimeSpan stalenessThreshold,
        ILogger<SolverDiscoveryService>? logger = null)
    {
        _http = http;
        _cacheTtl = cacheTtl;
        _stalenessThreshold = stalenessThreshold;
        _logger = logger;
    }

    /// <summary>The default registry index URL for a network name (<c>bitcoin</c>/<c>signet</c>/<c>mutinynet</c>).</summary>
    public static Uri RegistryFor(string network) => network switch
    {
        "bitcoin" => MainnetRegistry,
        "signet" => SignetRegistry,
        "mutinynet" => MutinynetRegistry,
        "regtest" => RegtestRegistry,
        _ => throw new ArgumentException($"Unknown network '{network}'.", nameof(network)),
    };

    /// <summary>Fetch a per-network index, cached for <see cref="_cacheTtl"/>.</summary>
    public async Task<GetSolverRegistryResponse> FetchIndexAsync(Uri registryUrl, CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(registryUrl, out var cached)
                && DateTimeOffset.UtcNow - cached.FetchedAt < _cacheTtl)
            {
                return cached.Index;
            }
        }

        var json = await _http.GetStringAsync(registryUrl, cancellationToken);
        var index = JsonSerializer.Deserialize<GetSolverRegistryResponse>(json, JsonOptions)
                    ?? throw new InvalidOperationException($"Empty registry index at {registryUrl}.");

        lock (_cacheLock)
        {
            _cache[registryUrl] = (DateTimeOffset.UtcNow, index);
        }
        return index;
    }

    /// <summary>
    /// Discover markets for <paramref name="network"/> across one or more registries plus any local
    /// cards. Registries whose version or network don't match are skipped; a stale index (generated
    /// more than <see cref="_stalenessThreshold"/> ago) is used but warned about.
    /// </summary>
    public async Task<IReadOnlyList<IndexedMarket>> DiscoverMarketsAsync(
        string network,
        IReadOnlyList<Uri>? registries = null,
        IReadOnlyList<SolverCard>? localCards = null,
        CancellationToken cancellationToken = default)
    {
        registries ??= [RegistryFor(network)];
        var markets = new List<IndexedMarket>();

        foreach (var registry in registries)
        {
            GetSolverRegistryResponse index;
            try
            {
                index = await FetchIndexAsync(registry, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Skipping registry {Registry}: fetch failed", registry);
                continue;
            }

            if (index.Version != SupportedVersion)
            {
                _logger?.LogWarning("Skipping registry {Registry}: version {Version} != {Supported}",
                    registry, index.Version, SupportedVersion);
                continue;
            }
            if (!string.Equals(index.Network, network, StringComparison.Ordinal))
            {
                _logger?.LogWarning("Skipping registry {Registry}: network '{Actual}' != '{Expected}'",
                    registry, index.Network, network);
                continue;
            }

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds((long)index.GeneratedAt);
            if (age > _stalenessThreshold)
            {
                _logger?.LogWarning("Registry {Registry} index is stale (generated {Age} ago)", registry, age);
            }

            markets.AddRange(index.Markets);
        }

        foreach (var card in localCards ?? [])
        {
            if (card.Version != SupportedVersion)
            {
                _logger?.LogWarning("Skipping local card '{Name}': version {Version} != {Supported}",
                    card.Name, card.Version, SupportedVersion);
                continue;
            }
            foreach (var market in card.Markets)
            {
                markets.Add(ToIndexed(market, card));
            }
        }

        return markets;
    }

    /// <summary>
    /// Filter discovered markets to one corridor-qualified pair and a base amount inside the bounds,
    /// cheapest first at that size.
    /// </summary>
    /// <param name="markets">The discovered markets.</param>
    /// <param name="baseAssetId">The base side's asset id — <c>btc</c> or the asset-id hex.</param>
    /// <param name="quoteAssetId">The quote side's asset id.</param>
    /// <param name="baseAmount">The size being traded, in base atomic units.</param>
    /// <param name="baseCorridor">The base side's rail; defaults to arkade.</param>
    /// <param name="quoteCorridor">The quote side's rail; defaults to arkade.</param>
    /// <returns>The matching markets, cheapest first.</returns>
    /// <remarks>
    /// <para>
    /// Identity is the corridor-qualified leg pair, never the ticker and no longer the bare id pair:
    /// a solver's <c>BTC/lightning:BTC</c> and <c>BTC/onchain:BTC</c> are both btc-against-btc, and
    /// matching on ids alone would offer a maker either one for a request naming a rail.
    /// </para>
    /// <para>
    /// Ranking is by the total fee at <paramref name="baseAmount"/>, not by <c>fee_bps</c>: a market
    /// with a lower spread and a flat fee is dearer at small sizes and cheaper at large ones, so the
    /// spread alone puts them in the wrong order at one end or the other.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<IndexedMarket> FilterAndRank(
        IEnumerable<IndexedMarket> markets,
        string baseAssetId,
        string quoteAssetId,
        long baseAmount,
        string? baseCorridor = null,
        string? quoteCorridor = null)
    {
        var wanted = $"{baseCorridor ?? SolverMarket.ArkadeCorridor}:{baseAssetId}"
                     + $"/{quoteCorridor ?? SolverMarket.ArkadeCorridor}:{quoteAssetId}";

        return markets
            .Where(m => m.PairKey() == wanted)
            .Where(m => m.MaxBaseAmount > 0 && baseAmount >= m.MinBaseAmount && baseAmount <= m.MaxBaseAmount)
            .OrderBy(m => m.TotalFeeOn(baseAmount))
            .ToList();
    }

    /// <summary>
    /// The market's price <c>P</c>, in quote atomic units per base atomic unit.
    /// </summary>
    /// <param name="market">The market to price.</param>
    /// <param name="cancellationToken">Cancels the feed fetch.</param>
    /// <returns>The normalized price.</returns>
    /// <exception cref="InvalidOperationException">The card declares no usable feed for a cross-asset market.</exception>
    /// <remarks>
    /// A same-asset market carries no feed fields at all and its price is identically 1 — that is
    /// the shape every corridor market between BTC and BTC has, so fetching unconditionally is how
    /// a client crashes on the first corridor entry it meets. A cross-asset market missing its feed
    /// is a malformed card instead, and says so.
    /// </remarks>
    public async Task<decimal> FetchPriceAsync(SolverMarket market, CancellationToken cancellationToken = default)
    {
        if (market.PriceFeed is not { Length: > 0 } feed || market.PriceFeedSchema is null)
        {
            return market.IsSameAsset
                ? 1m
                : throw new InvalidOperationException(
                    $"market '{market.Pair}' trades {market.BaseAsset.Id} against {market.QuoteAsset.Id} "
                    + "but declares no price feed, so it cannot be priced");
        }

        var body = await _http.GetStringAsync(feed, cancellationToken);
        var root = JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty price-feed response.");
        var scalar = ResolveJsonPointer(root, market.PriceFeedSchema.PricePath);
        return NormalizePrice(ReadScalar(scalar), market.PriceDecimals);
    }

    /// <summary>Normalize a raw feed scalar: divide by 10^<paramref name="priceDecimals"/>.</summary>
    /// <param name="raw">The scalar the feed served.</param>
    /// <param name="priceDecimals">The market's declared exponent.</param>
    /// <returns>The price in quote atomic units per base atomic unit.</returns>
    /// <remarks>
    /// There is no inversion step. A feed is always advertised in base/quote terms, so a market
    /// needing the other direction advertises the other feed.
    /// </remarks>
    public static decimal NormalizePrice(decimal raw, int priceDecimals) => raw / Pow10(priceDecimals);

    /// <summary>
    /// The maker pricing formula: <c>wantAmount = floor(D · P · (1 − (fee_bps + safety_bps)/10000))</c>,
    /// where <paramref name="depositBaseUnits"/> is <c>D</c> and <paramref name="price"/> is <c>P</c>.
    /// </summary>
    public static long ComputeWantAmount(
        long depositBaseUnits,
        decimal price,
        int feeBps,
        int safetyBps = DefaultSafetyBps)
    {
        var spread = 1m - (feeBps + safetyBps) / 10000m;
        return (long)Math.Floor(depositBaseUnits * price * spread);
    }

    /// <summary>
    /// Inverse of <see cref="ComputeWantAmount"/> — the deposit (base atomic units) a maker must
    /// fund to receive at least <paramref name="wantAmount"/> of the quote asset, at
    /// <paramref name="price"/> (atomic quote-per-base) after conceding <c>feeBps + safetyBps</c>.
    /// This is the <c>wantAmount</c> arm of the discovery-client's <c>quoteOffer</c> (the maker
    /// names the amount they want and gets quoted the required deposit). Rounds up so the resulting
    /// deposit never quotes short. Returns <c>0</c> when the spread is non-positive.
    /// </summary>
    public static long ComputeRequiredDeposit(
        long wantAmount,
        decimal price,
        int feeBps,
        int safetyBps = DefaultSafetyBps)
    {
        if (wantAmount <= 0 || price <= 0m) return 0;
        var net = 10000 - feeBps - safetyBps;
        if (net <= 0) return 0;
        var spread = net / 10000m;
        return (long)Math.Ceiling(wantAmount / (price * spread));
    }

    /// <summary>Resolve an RFC 6901 JSON Pointer (e.g. <c>"/price"</c>, <c>"/data/0/px"</c>) against a JSON tree.</summary>
    public static JsonNode ResolveJsonPointer(JsonNode root, string pointer)
    {
        if (pointer.Length == 0) return root;
        if (pointer[0] != '/') throw new FormatException($"Invalid JSON Pointer '{pointer}' — must start with '/'.");

        var node = root;
        foreach (var rawToken in pointer.Split('/').Skip(1))
        {
            var token = rawToken.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject obj => obj[token]
                    ?? throw new InvalidOperationException($"JSON Pointer '{pointer}': no member '{token}'."),
                JsonArray arr when int.TryParse(token, out var i) && i >= 0 && i < arr.Count => arr[i]!,
                _ => throw new InvalidOperationException($"JSON Pointer '{pointer}': cannot descend into '{token}'."),
            };
        }
        return node;
    }

    private static decimal ReadScalar(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String
            ? decimal.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture)
            : node.GetValue<decimal>();

    private static readonly decimal[] Pow10Table =
    [
        1e0m, 1e1m, 1e2m, 1e3m, 1e4m, 1e5m, 1e6m, 1e7m,
        1e8m, 1e9m, 1e10m, 1e11m, 1e12m, 1e13m, 1e14m,
        1e15m, 1e16m, 1e17m, 1e18m, 1e19m, 1e20m,
        1e21m, 1e22m, 1e23m, 1e24m, 1e25m, 1e26m, 1e27m, 1e28m
    ];
    // The exponent comes off a published card, so it is a stranger's: unchecked, an out-of-range
    // one surfaces as IndexOutOfRangeException from inside a price fetch, reading as a bug here
    // rather than as a card to decline.
    private static decimal Pow10(int n) =>
        n >= 0 && n < Pow10Table.Length
            ? Pow10Table[n]
            : throw new ArgumentOutOfRangeException(
                nameof(n), n, $"a market's decimals must be between 0 and {Pow10Table.Length - 1}");

    /// <summary>
    /// Tag a card's market with its solver, the way the reducer does for the published index.
    /// </summary>
    /// <remarks>
    /// Every field is carried across. A pinned card is the one route to a solver no registry lists,
    /// so a field dropped here is a market the caller can no longer tell apart — a corridor that
    /// reads as spot, or bounds and a rendezvous that vanish between the file and the caller.
    /// </remarks>
    private static IndexedMarket ToIndexed(SolverMarket m, SolverCard card) => new()
    {
        Solver = card.Name,
        DiscoveryPubkey = card.DiscoveryPubkey,
        Transports = card.Transports,
        Pair = m.Pair,
        BaseAsset = m.BaseAsset,
        QuoteAsset = m.QuoteAsset,
        BaseCorridor = m.BaseCorridor,
        QuoteCorridor = m.QuoteCorridor,
        PriceFeed = m.PriceFeed,
        PriceFeedSchema = m.PriceFeedSchema,
        PriceDecimals = m.PriceDecimals,
        FeeBps = m.FeeBps,
        FeeFlat = m.FeeFlat,
        MinBaseAmount = m.MinBaseAmount,
        MaxBaseAmount = m.MaxBaseAmount,
        MinQuoteAmount = m.MinQuoteAmount,
        MaxQuoteAmount = m.MaxQuoteAmount,
    };
}
