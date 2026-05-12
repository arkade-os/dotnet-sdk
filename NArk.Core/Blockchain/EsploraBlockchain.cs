using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace NArk.Blockchain;

/// <summary>
/// Unified Esplora-backed <see cref="IBitcoinBlockchain"/>. Talks to a stock
/// Esplora REST API (mempool.space, Chopsticks, Blockstream Esplora) — chain
/// time via <c>blocks/tip/hash</c>, UTXOs via <c>address/{addr}/utxo</c>,
/// broadcast via <c>POST /tx</c>, status via <c>tx/{id}/status</c>, fee
/// estimates via <c>fee-estimates</c>.
/// <para>
/// Esplora has no package-relay endpoint; <see cref="BroadcastPackageAsync"/>
/// falls back to sequential parent-then-child broadcast.
/// </para>
/// </summary>
public class EsploraBlockchain : IBitcoinBlockchain
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    public EsploraBlockchain(Uri baseUri, ILogger<EsploraBlockchain>? logger = null)
    {
        _httpClient = new HttpClient { BaseAddress = baseUri };
        _logger = logger;
    }

    public EsploraBlockchain(HttpClient httpClient, ILogger<EsploraBlockchain>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // ── Chain time ───────────────────────────────────────────────────

    public async Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        var tipHashResponse = await _httpClient.GetAsync("blocks/tip/hash", cancellationToken);
        tipHashResponse.EnsureSuccessStatusCode();
        var tipHash = (await tipHashResponse.Content.ReadAsStringAsync(cancellationToken)).Trim();

        var blockResponse = await _httpClient.GetAsync($"block/{tipHash}", cancellationToken);
        blockResponse.EnsureSuccessStatusCode();
        var block = await blockResponse.Content.ReadFromJsonAsync<EsploraBlockResponse>(cancellationToken)
            ?? throw new Exception("Esplora API returned invalid json when retrieving block information");

        return new TimeHeight(
            DateTimeOffset.FromUnixTimeSeconds(block.MedianTime),
            (uint)block.Height);
    }

    // ── UTXO lookup ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<BoardingUtxo>> GetUtxosAsync(string address, CancellationToken cancellationToken = default)
    {
        var utxos = await _httpClient.GetFromJsonAsync<EsploraUtxo[]>(
            $"address/{address}/utxo", cancellationToken);

        if (utxos is null)
            return [];

        return utxos.Select(u => new BoardingUtxo(
            Txid: u.Txid,
            Vout: (uint)u.Vout,
            Amount: (ulong)u.Value,
            Confirmed: u.Status?.Confirmed ?? false,
            BlockHeight: u.Status?.BlockHeight ?? 0,
            BlockTime: u.Status?.BlockTime ?? 0
        )).ToArray();
    }

    // ── Broadcast ────────────────────────────────────────────────────

    public async Task<bool> BroadcastAsync(Transaction tx, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = new StringContent(tx.ToHex(), Encoding.UTF8, "text/plain");
            var response = await _httpClient.PostAsync("tx", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogWarning("Esplora broadcast failed for tx {Txid}: {Error}",
                    tx.GetHash(), error);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Failed to broadcast tx {Txid} via Esplora", tx.GetHash());
            return false;
        }
    }

    public async Task<bool> BroadcastPackageAsync(Transaction parent, Transaction child, CancellationToken cancellationToken = default)
    {
        // Standard Esplora does not expose a package relay endpoint.
        // Fall back to sequential broadcast: parent first, then CPFP child.
        _logger?.LogDebug("Esplora: broadcasting package as sequential txs (parent then child)");

        var parentSuccess = await BroadcastAsync(parent, cancellationToken);
        if (!parentSuccess)
        {
            _logger?.LogWarning("Esplora package fallback: parent broadcast failed for {Txid}", parent.GetHash());
            return false;
        }

        var childSuccess = await BroadcastAsync(child, cancellationToken);
        if (!childSuccess)
        {
            _logger?.LogWarning(
                "Esplora package fallback: child broadcast failed for {Txid} (parent {ParentTxid} was accepted)",
                child.GetHash(), parent.GetHash());
            // Parent was accepted so return true — child may be rejected if parent
            // fee is sufficient on its own, which is still a valid outcome.
            return true;
        }
        return true;
    }

    // ── Tx status ────────────────────────────────────────────────────

    public async Task<TxStatus> GetTxStatusAsync(uint256 txid, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"tx/{txid}/status", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new TxStatus(false, null, false);

            var status = await response.Content.ReadFromJsonAsync<EsploraTxStatus>(cancellationToken: cancellationToken);
            if (status is null)
                return new TxStatus(false, null, false);

            return new TxStatus(
                status.Confirmed,
                status.Confirmed ? (uint?)status.BlockHeight : null,
                !status.Confirmed);
        }
        catch
        {
            return new TxStatus(false, null, false);
        }
    }

    // ── Fee estimate ─────────────────────────────────────────────────

    public async Task<FeeRate> EstimateFeeRateAsync(int confirmTarget = 6, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("fee-estimates", cancellationToken);
            response.EnsureSuccessStatusCode();

            var estimates = await response.Content.ReadFromJsonAsync<Dictionary<string, double>>(cancellationToken: cancellationToken);
            if (estimates is null)
                return new FeeRate(Money.Satoshis(2));

            var targetStr = confirmTarget.ToString();
            if (estimates.TryGetValue(targetStr, out var rate))
                return new FeeRate(Money.Satoshis((long)Math.Ceiling(rate)));

            var closest = estimates
                .Select(kvp => (Target: int.TryParse(kvp.Key, out var t) ? t : int.MaxValue, Rate: kvp.Value))
                .Where(x => x.Target != int.MaxValue)
                .OrderBy(x => Math.Abs(x.Target - confirmTarget))
                .FirstOrDefault();

            return closest.Rate > 0
                ? new FeeRate(Money.Satoshis((long)Math.Ceiling(closest.Rate)))
                : new FeeRate(Money.Satoshis(2));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Failed to estimate fee rate via Esplora, using fallback");
            return new FeeRate(Money.Satoshis(2));
        }
    }

    private class EsploraBlockResponse
    {
        [JsonPropertyName("height")] public long Height { get; set; }
        [JsonPropertyName("mediantime")] public long MedianTime { get; set; }
    }

    private class EsploraUtxo
    {
        [JsonPropertyName("txid")] public string Txid { get; set; } = string.Empty;
        [JsonPropertyName("vout")] public int Vout { get; set; }
        [JsonPropertyName("value")] public long Value { get; set; }
        [JsonPropertyName("status")] public EsploraUtxoStatus? Status { get; set; }
    }

    private class EsploraUtxoStatus
    {
        [JsonPropertyName("confirmed")] public bool Confirmed { get; set; }
        [JsonPropertyName("block_height")] public long BlockHeight { get; set; }
        [JsonPropertyName("block_time")] public long BlockTime { get; set; }
    }

    private class EsploraTxStatus
    {
        [JsonPropertyName("confirmed")] public bool Confirmed { get; set; }
        [JsonPropertyName("block_height")] public long BlockHeight { get; set; }
    }
}
