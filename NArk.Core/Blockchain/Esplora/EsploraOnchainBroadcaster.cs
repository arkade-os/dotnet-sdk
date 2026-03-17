using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace NArk.Blockchain.Esplora;

/// <summary>
/// Broadcasts transactions via Esplora REST API.
/// Supports single tx broadcast and package relay.
/// </summary>
public class EsploraOnchainBroadcaster : IOnchainBroadcaster
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    public EsploraOnchainBroadcaster(Uri baseUri, ILogger<EsploraOnchainBroadcaster>? logger = null)
    {
        _httpClient = new HttpClient { BaseAddress = baseUri };
        _logger = logger;
    }

    public EsploraOnchainBroadcaster(HttpClient httpClient, ILogger<EsploraOnchainBroadcaster>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

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

    public async Task<bool> BroadcastPackageAsync(
        Transaction parent, Transaction child, CancellationToken cancellationToken = default)
    {
        try
        {
            var package = new[] { parent.ToHex(), child.ToHex() };
            var response = await _httpClient.PostAsJsonAsync("txs/package", package, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogWarning("Esplora package broadcast failed: {Error}", error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Failed to broadcast package via Esplora");
            return false;
        }
    }

    public async Task<TxStatus> GetTxStatusAsync(
        uint256 txid, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"tx/{txid}/status", cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new TxStatus(false, null, false);

            var status = await response.Content.ReadFromJsonAsync<EsploraTxStatus>(
                cancellationToken: cancellationToken);

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

    public async Task<FeeRate> EstimateFeeRateAsync(
        int confirmTarget = 6, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("fee-estimates", cancellationToken);
            response.EnsureSuccessStatusCode();

            var estimates = await response.Content.ReadFromJsonAsync<Dictionary<string, double>>(
                cancellationToken: cancellationToken);

            if (estimates is null)
                return new FeeRate(Money.Satoshis(2));

            // Find the closest target
            var targetStr = confirmTarget.ToString();
            if (estimates.TryGetValue(targetStr, out var rate))
                return new FeeRate(Money.Satoshis((long)Math.Ceiling(rate)));

            // Fallback to nearest available target
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

    private class EsploraTxStatus
    {
        [JsonPropertyName("confirmed")]
        public bool Confirmed { get; set; }

        [JsonPropertyName("block_height")]
        public long BlockHeight { get; set; }
    }
}
