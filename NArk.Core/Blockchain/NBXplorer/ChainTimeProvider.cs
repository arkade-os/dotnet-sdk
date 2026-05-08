using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBXplorer;

namespace NArk.Blockchain.NBXplorer;

/// <summary>
/// Adapter that wires <see cref="RPCChainTimeProvider"/> against an
/// NBXplorer <see cref="ExplorerClient"/>. Logger is optional and only
/// used to surface warnings when the inner provider falls back to its
/// cached chain time after a transient RPC failure.
/// </summary>
public class ChainTimeProvider : IChainTimeProvider
{
    private readonly RPCChainTimeProvider _innerRpcProvider;

    public ChainTimeProvider(Network network, Uri uri, ILogger<RPCChainTimeProvider>? logger = null)
    {
        var client = new ExplorerClient(new NBXplorerNetworkProvider(network.ChainName).GetBTC(), uri);
        _innerRpcProvider = new RPCChainTimeProvider(client.RPCClient, logger);
    }

    public ChainTimeProvider(ExplorerClient explorerClient, ILogger<RPCChainTimeProvider>? logger = null)
    {
        _innerRpcProvider = new RPCChainTimeProvider(explorerClient.RPCClient, logger);
    }

    public ChainTimeProvider(IOptions<ChainTimeProviderOptions> options, ILogger<RPCChainTimeProvider>? logger = null)
        : this(options.Value.Network, options.Value.Uri, logger) { }


    public Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        return _innerRpcProvider.GetChainTime(cancellationToken);
    }
}
