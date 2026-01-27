using NArk.Abstractions.Blockchain;
using NBitcoin.RPC;
using Newtonsoft.Json;

namespace NArk.Blockchain.NBXplorer;

public class RPCChainTimeProvider : IChainTimeProvider
{
    private readonly RPCClient _client;

    public RPCChainTimeProvider(RPCClient client)
    {
        _client = client;
    }
    public async Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendCommandAsync("getblockchaininfo", cancellationToken);
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