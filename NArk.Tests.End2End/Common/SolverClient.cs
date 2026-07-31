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

    /// <summary><c>GET /v1/pairs</c> — the trading pairs the solver honors.</summary>
    public async Task<IReadOnlyList<SolverPair>> ListPairsAsync(CancellationToken cancellationToken = default)
        => (await GetAsync<ListPairsResponse>("v1/pairs", cancellationToken))?.Pairs ?? [];

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

    public sealed record SolverPair
    {
        /// <summary>Display pair, e.g. <c>"BTC/&lt;assetId&gt;"</c>. The asset side is the non-BTC id.</summary>
        public string Pair { get; init; } = "";
        public string PriceFeed { get; init; } = "";
        public ulong MinAmount { get; init; }
        public ulong MaxAmount { get; init; }
    }

    public sealed record SolverTrade
    {
        public string Pair { get; init; } = "";
        public string DepositAsset { get; init; } = "";
        public ulong DepositAmount { get; init; }
        public string WantAsset { get; init; } = "";
        public ulong WantAmount { get; init; }
        public string OfferTxid { get; init; } = "";
        public string FulfillTxid { get; init; } = "";
    }

    private sealed record StatusResponse { public bool Running { get; init; } }
    private sealed record ListPairsResponse { public List<SolverPair> Pairs { get; init; } = []; }
    private sealed record BalanceResponse { public Dictionary<string, ulong> AssetBalances { get; init; } = new(); }
    private sealed record ListTradesResponse { public List<SolverTrade> Trades { get; init; } = []; }
}
