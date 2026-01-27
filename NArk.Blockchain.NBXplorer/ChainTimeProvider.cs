using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBXplorer;

namespace NArk.Blockchain.NBXplorer;

public class ChainTimeProvider : IChainTimeProvider
{
    private readonly RPCChainTimeProvider _innerRpcProvider;

    public ChainTimeProvider(Network network, Uri uri)
    {
        var client = new ExplorerClient(new NBXplorerNetworkProvider(network.ChainName).GetBTC(), uri);
        _innerRpcProvider = new RPCChainTimeProvider(client.RPCClient);

    }

    public ChainTimeProvider(ExplorerClient explorerClient)
    {
        _innerRpcProvider = new RPCChainTimeProvider(explorerClient.RPCClient);
    }

    public ChainTimeProvider(IOptions<ChainTimeProviderOptions> options)
        : this(options.Value.Network, options.Value.Uri) { }


    public Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        return _innerRpcProvider.GetChainTime(cancellationToken);
    }
}