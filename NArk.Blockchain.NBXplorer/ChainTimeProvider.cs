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