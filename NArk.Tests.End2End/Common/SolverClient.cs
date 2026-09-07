using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Minimal read client for the regtest solver's REST API (grpc-gateway on host port 7091 →
/// container 7171). Used by the fill E2E to learn the seeded market/asset and confirm the solver
/// is live with inventory. Not part of the SDK — production makers discover markets via the git
/// registry (<c>SolverDiscoveryService</c>), not the solver's own admin API.
/// </summary>
public sealed class SolverClient
{
    // solverd's grpc-gateway emits snake_case JSON (asset_balances, min_amount, price_feed, …).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public SolverClient(Uri baseUrl) => _http = new HttpClient { BaseAddress = baseUrl };

    /// <summary><c>GET /v1/status</c> — true when the solver bot is running.</summary>
    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await GetAsync<StatusResponse>("v1/status", cancellationToken))?.Running == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary><c>GET /v1/markets</c> — the markets the solver honors.</summary>
    /// <remarks>
    /// Was <c>GET /v1/pairs</c>. solverd renamed the concept: a market names its two assets
    /// separately rather than as one <c>"BTC/&lt;asset&gt;"</c> string, and the old path now 404s.
    /// </remarks>
    public async Task<IReadOnlyList<SolverMarket>> ListMarketsAsync(CancellationToken cancellationToken = default)
        => (await GetAsync<ListMarketsResponse>("v1/markets", cancellationToken))?.Markets ?? [];

    /// <summary><c>GET /v1/balance</c> — the solver's asset inventory, keyed by asset id.</summary>
    public async Task<IReadOnlyDictionary<string, ulong>> GetAssetBalancesAsync(CancellationToken cancellationToken = default)
        => (await GetAsync<BalanceResponse>("v1/balance", cancellationToken))?.AssetBalances
           ?? new Dictionary<string, ulong>();

    /// <summary><c>GET /v1/trades</c> — trades the solver has processed (with fulfill txids).</summary>
    public async Task<IReadOnlyList<SolverTrade>> ListTradesAsync(CancellationToken cancellationToken = default)
        => (await GetAsync<ListTradesResponse>("v1/trades", cancellationToken))?.Trades ?? [];

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var body = await _http.GetStringAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    /// <summary>One market as <c>GET /v1/markets</c> reports it.</summary>
    /// <remarks>
    /// Read off the wire rather than from the proto's comments, which describe the two amount
    /// ranges by trade direction and are easy to read backwards. The bounds here are named for
    /// the asset they are denominated in: a deposit of sats is a BASE amount on a <c>BTC/…</c>
    /// market, so that is the pair to clamp it against.
    /// </remarks>
    public sealed record SolverMarket
    {
        /// <summary>The base asset's id — <c>"BTC"</c> on the seeded regtest market.</summary>
        public string BaseAsset { get; init; } = "";

        /// <summary>The quote asset's id, hex for an Arkade-issued asset.</summary>
        public string QuoteAsset { get; init; } = "";

        public int BaseDecimals { get; init; }
        public string PriceFeed { get; init; } = "";
        public ulong MinBaseAmount { get; init; }
        public ulong MaxBaseAmount { get; init; }
        public ulong MinQuoteAmount { get; init; }
        public ulong MaxQuoteAmount { get; init; }
    }

    public sealed record SolverTrade
    {
        /// <summary>The market this trade was on, as <c>"&lt;base&gt;/&lt;quote&gt;"</c>.</summary>
        public string Market { get; init; } = "";
        public string DepositAsset { get; init; } = "";
        public ulong DepositAmount { get; init; }
        public string WantAsset { get; init; } = "";
        public ulong WantAmount { get; init; }
        public string OfferTxid { get; init; } = "";
        public string FulfillTxid { get; init; } = "";
    }

    private sealed record StatusResponse { public bool Running { get; init; } }
    private sealed record ListMarketsResponse { public List<SolverMarket> Markets { get; init; } = []; }
    private sealed record BalanceResponse { public Dictionary<string, ulong> AssetBalances { get; init; } = new(); }
    private sealed record ListTradesResponse { public List<SolverTrade> Trades { get; init; } = []; }
}
