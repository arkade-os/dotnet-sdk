using System.Net.Http.Json;
using NArk.Abstractions.Blockchain;

namespace NArk.Blockchain.NBXplorer;

/// <summary>
/// Chain time provider using Esplora API (https://github.com/Blockstream/esplora/blob/master/API.md)
/// </summary>
public class EsploraChainTimeProvider : IChainTimeProvider
{
    private readonly HttpClient _httpClient;

    public EsploraChainTimeProvider(Uri baseUri)
    {
        _httpClient = new HttpClient { BaseAddress = baseUri };
    }

    public EsploraChainTimeProvider(Uri baseUri, HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = baseUri;
    }

    public async Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        // Get the tip block hash
        var tipHashResponse = await _httpClient.GetAsync("blocks/tip/hash", cancellationToken);
        tipHashResponse.EnsureSuccessStatusCode();
        var tipHash = await tipHashResponse.Content.ReadAsStringAsync(cancellationToken);

        // Get block info which includes height and mediantime
        var blockResponse = await _httpClient.GetAsync($"block/{tipHash.Trim()}", cancellationToken);
        blockResponse.EnsureSuccessStatusCode();
        var block = await blockResponse.Content.ReadFromJsonAsync<EsploraBlockResponse>(cancellationToken);

        if (block is null)
            throw new Exception("Esplora API returned invalid json when retrieving block information");

        return new TimeHeight(
            DateTimeOffset.FromUnixTimeSeconds(block.MedianTime),
            (uint)block.Height
        );
    }

    internal class EsploraBlockResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("height")]
        public long Height { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("mediantime")]
        public long MedianTime { get; set; }
    }
}