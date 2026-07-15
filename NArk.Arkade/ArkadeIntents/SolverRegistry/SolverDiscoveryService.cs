using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NArk.Arkade.NonInteractiveSwaps;

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
    public static readonly Uri MainnetRegistry = new("https://arkade-os.github.io/solver-registry/bitcoin.json");
    public static readonly Uri SignetRegistry = new("https://arkade-os.github.io/solver-registry/signet.json");
    public static readonly Uri MutinynetRegistry = new("https://arkade-os.github.io/solver-registry/mutinynet.json");

    /// <summary>The only discovery protocol version this client understands.</summary>
    public const int SupportedVersion = 0;

    /// <summary>Suggested default client-side safety cushion, in basis points.</summary>
    public const int DefaultSafetyBps = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
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
                markets.Add(ToIndexed(market, card.Name, card.DiscoveryPubkey));
            }
        }

        return markets;
    }

    /// <summary>
    /// Filter discovered markets to the given id pair and a base amount that fits the size bounds,
    /// ranked cheapest first (ascending <c>fee_bps</c>). Identity is the asset-id pair, not the ticker.
    /// </summary>
    public static IReadOnlyList<IndexedMarket> FilterAndRank(
        IEnumerable<IndexedMarket> markets,
        string baseAssetId,
        string quoteAssetId,
        long baseAmount) =>
        markets
            .Where(m => m.BaseAsset.Id == baseAssetId && m.QuoteAsset.Id == quoteAssetId)
            .Where(m => baseAmount >= m.MinBaseAmount && baseAmount <= m.MaxBaseAmount)
            .OrderBy(m => m.FeeBps)
            .ToList();

    /// <summary>
    /// Fetch the market's price feed and return the normalized price <c>P</c> (quote units per base
    /// unit): the scalar at <see cref="PriceFeedSchema.PricePath"/>, divided by 10^<c>price_decimals</c>,
    /// inverted if <c>invert</c> is set.
    /// </summary>
    public async Task<decimal> FetchPriceAsync(SolverMarket market, CancellationToken cancellationToken = default)
    {
        var body = await _http.GetStringAsync(market.PriceFeed, cancellationToken);
        var root = JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty price-feed response.");
        var scalar = ResolveJsonPointer(root, market.PriceFeedSchema.PricePath);
        return NormalizePrice(ReadScalar(scalar), market.PriceDecimals, market.Invert);
    }

    /// <summary>Normalize a raw feed scalar into a price: divide by 10^<paramref name="priceDecimals"/>, then invert if requested.</summary>
    public static decimal NormalizePrice(decimal raw, int priceDecimals, bool invert)
    {
        var scaled = raw / Pow10(priceDecimals);
        return invert ? 1m / scaled : scaled;
    }

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

    private static decimal Pow10(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++) result *= 10m;
        return result;
    }

    private static IndexedMarket ToIndexed(SolverMarket m, string solver, string? discoveryPubkey) => new()
    {
        Solver = solver,
        DiscoveryPubkey = discoveryPubkey,
        Pair = m.Pair,
        BaseAsset = m.BaseAsset,
        QuoteAsset = m.QuoteAsset,
        PriceFeed = m.PriceFeed,
        PriceFeedSchema = m.PriceFeedSchema,
        PriceDecimals = m.PriceDecimals,
        Invert = m.Invert,
        FeeBps = m.FeeBps,
        MinBaseAmount = m.MinBaseAmount,
        MaxBaseAmount = m.MaxBaseAmount,
    };
}
