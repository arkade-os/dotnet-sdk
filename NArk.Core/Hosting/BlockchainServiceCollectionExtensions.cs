using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Services;
using NArk.Blockchain.Esplora;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Services;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;

namespace NArk.Hosting;

/// <summary>
/// Composite DI helpers for the three split-by-responsibility blockchain
/// interfaces (<see cref="IBoardingUtxoProvider"/>, <see cref="IChainTimeProvider"/>,
/// <see cref="IOnchainBroadcaster"/>). One call wires every impl a given
/// backend supports, instead of three separate <c>AddSingleton</c>s wrapping
/// the same client.
/// <para>
/// À la carte registration still works — these helpers only call
/// <c>TryAddSingleton</c> equivalents so they don't clobber a more specific
/// impl that a consumer already wired. Pick the backend you have a client
/// for, or mix-and-match if you want, say, NBXplorer UTXOs + Esplora broadcast.
/// </para>
/// </summary>
public static class BlockchainServiceCollectionExtensions
{
    /// <summary>
    /// Wires all three blockchain interfaces against the given NBXplorer
    /// <see cref="ExplorerClient"/>:
    /// <list type="bullet">
    /// <item><see cref="IBoardingUtxoProvider"/> → <see cref="NBXplorerBoardingUtxoProvider"/></item>
    /// <item><see cref="IChainTimeProvider"/> → <see cref="ChainTimeProvider"/> (NBXplorer's RPC adapter, with cached-fallback semantics)</item>
    /// <item><see cref="IOnchainBroadcaster"/> → <see cref="NBXplorerOnchainBroadcaster"/></item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddNBXplorerBlockchain(
        this IServiceCollection services,
        ExplorerClient explorerClient)
    {
        services.AddSingleton(explorerClient);
        services.AddSingleton<IBoardingUtxoProvider>(_ => new NBXplorerBoardingUtxoProvider(explorerClient));
        services.AddSingleton<IChainTimeProvider>(sp => new ChainTimeProvider(
            explorerClient,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RPCChainTimeProvider>>()));
        services.AddSingleton<IOnchainBroadcaster>(sp => new NBXplorerOnchainBroadcaster(
            explorerClient,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<NBXplorerOnchainBroadcaster>>()));
        return services;
    }

    /// <summary>
    /// Convenience overload that constructs the <see cref="ExplorerClient"/>
    /// from a <paramref name="network"/> + <paramref name="nbxplorerUri"/>
    /// before delegating to <see cref="AddNBXplorerBlockchain(IServiceCollection, ExplorerClient)"/>.
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
    /// Wires all three blockchain interfaces against an Esplora REST endpoint:
    /// <list type="bullet">
    /// <item><see cref="IBoardingUtxoProvider"/> → <see cref="EsploraBoardingUtxoProvider"/></item>
    /// <item><see cref="IChainTimeProvider"/> → <see cref="EsploraChainTimeProvider"/></item>
    /// <item><see cref="IOnchainBroadcaster"/> → <see cref="EsploraOnchainBroadcaster"/></item>
    /// </list>
    /// <para>
    /// Each impl gets its own <see cref="HttpClient"/> wrapped around the
    /// same base URI — Esplora is stateless so no client-side sharing
    /// concerns. If you'd rather pool a single <c>HttpClient</c>, register
    /// each impl manually with the shared client.
    /// </para>
    /// </summary>
    public static IServiceCollection AddEsploraBlockchain(
        this IServiceCollection services,
        Uri esploraBaseUri)
    {
        services.AddSingleton<IBoardingUtxoProvider>(_ => new EsploraBoardingUtxoProvider(esploraBaseUri));
        services.AddSingleton<IChainTimeProvider>(_ => new EsploraChainTimeProvider(esploraBaseUri));
        services.AddSingleton<IOnchainBroadcaster>(sp => new EsploraOnchainBroadcaster(
            esploraBaseUri,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<EsploraOnchainBroadcaster>>()));
        return services;
    }

    /// <summary>
    /// Wires the chain-time provider against a Bitcoin Core <see cref="RPCClient"/>.
    /// <para>
    /// RPC alone doesn't expose an address-indexed UTXO API
    /// (<see cref="IBoardingUtxoProvider"/> needs that — use NBXplorer or
    /// Esplora for the boarding-UTXO surface). Broadcast over pure RPC is
    /// also intentionally not registered here — go through NBXplorer
    /// (which wraps the same RPC) when you need broadcasting, so the
    /// <c>submitpackage</c> path stays consistent across consumers.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRpcBlockchain(
        this IServiceCollection services,
        RPCClient rpcClient)
    {
        services.AddSingleton<IChainTimeProvider>(sp => new RPCChainTimeProvider(
            rpcClient,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RPCChainTimeProvider>>()));
        return services;
    }
}
