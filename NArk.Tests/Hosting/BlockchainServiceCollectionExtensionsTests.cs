using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Services;
using NArk.Blockchain.Esplora;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Services;
using NArk.Hosting;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;

namespace NArk.Tests.Hosting;

[TestFixture]
public class BlockchainServiceCollectionExtensionsTests
{
    private static ExplorerClient NewExplorerClient() =>
        new(new NBXplorerNetworkProvider(ChainName.Regtest).GetBTC(), new Uri("http://localhost:39372"));

    [Test]
    public void AddNBXplorerBlockchain_RegistersAllThreeInterfaces()
    {
        var services = new ServiceCollection();
        services.AddNBXplorerBlockchain(NewExplorerClient());
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<IBoardingUtxoProvider>(), Is.TypeOf<NBXplorerBoardingUtxoProvider>());
        Assert.That(sp.GetService<IChainTimeProvider>(), Is.TypeOf<ChainTimeProvider>());
        Assert.That(sp.GetService<IOnchainBroadcaster>(), Is.TypeOf<NBXplorerOnchainBroadcaster>());
    }

    [Test]
    public void AddNBXplorerBlockchain_NetworkUriOverload_BuildsClient()
    {
        var services = new ServiceCollection();
        services.AddNBXplorerBlockchain(Network.RegTest, new Uri("http://localhost:39372"));
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<ExplorerClient>(), Is.Not.Null,
            "Network+Uri overload should construct + register an ExplorerClient");
        Assert.That(sp.GetService<IBoardingUtxoProvider>(), Is.TypeOf<NBXplorerBoardingUtxoProvider>());
        Assert.That(sp.GetService<IChainTimeProvider>(), Is.TypeOf<ChainTimeProvider>());
        Assert.That(sp.GetService<IOnchainBroadcaster>(), Is.TypeOf<NBXplorerOnchainBroadcaster>());
    }

    [Test]
    public void AddEsploraBlockchain_RegistersAllThreeInterfaces()
    {
        var services = new ServiceCollection();
        services.AddEsploraBlockchain(new Uri("http://localhost:30000"));
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<IBoardingUtxoProvider>(), Is.TypeOf<EsploraBoardingUtxoProvider>());
        Assert.That(sp.GetService<IChainTimeProvider>(), Is.TypeOf<EsploraChainTimeProvider>());
        Assert.That(sp.GetService<IOnchainBroadcaster>(), Is.TypeOf<EsploraOnchainBroadcaster>());
    }

    [Test]
    public void AddRpcBlockchain_RegistersChainTimeProviderOnly()
    {
        // RPC has no IBoardingUtxoProvider impl (no address indexer) and
        // broadcast is intentionally routed through NBXplorer instead.
        var services = new ServiceCollection();
        services.AddRpcBlockchain(new RPCClient(RPCCredentialString.Parse("user:pass"), new Uri("http://localhost:8332"), Network.RegTest));
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<IChainTimeProvider>(), Is.TypeOf<RPCChainTimeProvider>());
        Assert.That(sp.GetService<IBoardingUtxoProvider>(), Is.Null,
            "RPC composite should not register a boarding-UTXO provider");
        Assert.That(sp.GetService<IOnchainBroadcaster>(), Is.Null,
            "RPC composite should not register a broadcaster (use NBXplorer for broadcast)");
    }

    [Test]
    public void Composites_CanBeMixed_LastRegistrationWins()
    {
        // À la carte mixing: NBXplorer for UTXOs + Esplora for chain time.
        // ServiceCollection's standard semantics (last-wins on resolution)
        // make this work naturally — the composites don't lock anything in.
        var services = new ServiceCollection();
        services.AddNBXplorerBlockchain(NewExplorerClient());
        services.AddSingleton<IChainTimeProvider>(_ => new EsploraChainTimeProvider(new Uri("http://localhost:30000")));
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<IBoardingUtxoProvider>(), Is.TypeOf<NBXplorerBoardingUtxoProvider>());
        Assert.That(sp.GetService<IChainTimeProvider>(), Is.TypeOf<EsploraChainTimeProvider>(),
            "Last AddSingleton registration should win on IChainTimeProvider resolution");
    }
}
