using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Blockchain;
using NArk.Blockchain;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;

namespace NArk.Hosting;

/// <summary>
/// DI helpers for registering <see cref="IBitcoinBlockchain"/> implementations.
/// Pick the backend that matches your deployment — every implementation handles
/// all five blockchain operations (chain time, UTXO lookup, broadcast, package
/// broadcast, tx status, fee estimate), so a single line wires the entire
/// blockchain surface.
/// </summary>
public static class BlockchainServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="NBXplorerBlockchain"/> as the <see cref="IBitcoinBlockchain"/>
    /// against the given <paramref name="explorerClient"/>. Supports every
    /// blockchain operation (chain time, UTXO lookup, broadcast, tx status,
    /// fee estimate).
    /// </summary>
    public static IServiceCollection AddNBXplorerBlockchain(
        this IServiceCollection services,
        ExplorerClient explorerClient)
    {
        services.AddSingleton(explorerClient);
        services.AddSingleton<IBitcoinBlockchain>(sp => new NBXplorerBlockchain(
            explorerClient,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<NBXplorerBlockchain>>()));
        return services;
    }

    /// <summary>
    /// Convenience overload that constructs the <see cref="ExplorerClient"/>
    /// from a <paramref name="network"/> + <paramref name="nbxplorerUri"/>.
    /// </summary>
    public static IServiceCollection AddNBXplorerBlockchain(
        this IServiceCollection services,
        Network network,
        Uri nbxplorerUri)
    {
        var client = new ExplorerClient(new NBXplorerNetworkProvider(network.ChainName).GetBTC(), nbxplorerUri);
        return services.AddNBXplorerBlockchain(client);
    }

    /// <summary>
    /// Registers <see cref="EsploraBlockchain"/> against an Esplora REST endpoint.
    /// Supports every blockchain operation; package relay falls back to
    /// sequential parent-then-child broadcast (Esplora has no package endpoint).
    /// </summary>
    public static IServiceCollection AddEsploraBlockchain(
        this IServiceCollection services,
        Uri esploraBaseUri)
    {
        services.AddSingleton<IBitcoinBlockchain>(sp => new EsploraBlockchain(
            esploraBaseUri,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<EsploraBlockchain>>()));
        return services;
    }

    /// <summary>
    /// Registers <see cref="RpcBlockchain"/> against a Bitcoin Core
    /// <see cref="RPCClient"/>. Supports every blockchain operation EXCEPT
    /// <see cref="IBitcoinBlockchain.GetUtxosAsync"/> — Bitcoin Core has no
    /// native address-indexed UTXO API. Pair with NBXplorer or Esplora when
    /// boarding-UTXO discovery is required.
    /// </summary>
    public static IServiceCollection AddRpcBlockchain(
        this IServiceCollection services,
        RPCClient rpcClient)
    {
        services.AddSingleton<IBitcoinBlockchain>(sp => new RpcBlockchain(
            rpcClient,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RpcBlockchain>>()));
        return services;
    }
}
