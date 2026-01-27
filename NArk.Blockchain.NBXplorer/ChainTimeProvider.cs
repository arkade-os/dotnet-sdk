using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json;

namespace NArk.Blockchain.NBXplorer;

public class ChainTimeProvider : IChainTimeProvider
{
    private readonly ExplorerClient _client;

    public ChainTimeProvider(Network network, Uri uri)
    {
        _client = new ExplorerClient(new NBXplorerNetworkProvider(network.ChainName).GetBTC(), uri);
    }

    public ChainTimeProvider(ExplorerClient explorerClient)
    {
        _client = explorerClient;
    }

    public ChainTimeProvider(IOptions<ChainTimeProviderOptions> options)
        : this(options.Value.Network, options.Value.Uri) { }

    public async Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        var response = await _client.RPCClient.SendCommandAsync("getblockchaininfo", cancellationToken);
        if (response is null)
            throw new Exception("NBXplorer RPC returned null when retrieving chain information");
        var info = JsonConvert.DeserializeObject<GetBlockchainInfoResponse>(response.ResultString);
        if (info is null)
            throw new Exception("NBXplorer RPC returned invalid json when retrieving chain information");
        return new TimeHeight(
            DateTimeOffset.FromUnixTimeSeconds(info.MedianTime),
            info.Blocks
        );
    }

    internal class GetBlockchainInfoResponse
    {
        [JsonProperty("blocks")] public uint Blocks { get; set; }

        [JsonProperty("mediantime")] public long MedianTime { get; set; }
    }

}

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