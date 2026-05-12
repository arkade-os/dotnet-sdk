using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Blockchain;
using NArk.Blockchain;
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
    public void AddNBXplorerBlockchain_RegistersNBXplorerImpl()
    {
        var services = new ServiceCollection();
        services.AddNBXplorerBlockchain(NewExplorerClient());
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<IBitcoinBlockchain>(), Is.TypeOf<NBXplorerBlockchain>());
    }

    [Test]
    public void AddNBXplorerBlockchain_NetworkUriOverload_BuildsClient()
    {
        var services = new ServiceCollection();
        services.AddNBXplorerBlockchain(Network.RegTest, new Uri("http://localhost:39372"));
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<ExplorerClient>(), Is.Not.Null,
            "Network+Uri overload should construct + register an ExplorerClient");
        Assert.That(sp.GetService<IBitcoinBlockchain>(), Is.TypeOf<NBXplorerBlockchain>());
    }

    [Test]
    public void AddEsploraBlockchain_RegistersEsploraImpl()
    {
        var services = new ServiceCollection();
        services.AddEsploraBlockchain(new Uri("http://localhost:30000"));
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<IBitcoinBlockchain>(), Is.TypeOf<EsploraBlockchain>());
    }

    [Test]
    public void AddRpcBlockchain_RegistersRpcImpl()
    {
        var services = new ServiceCollection();
        services.AddRpcBlockchain(new RPCClient(RPCCredentialString.Parse("user:pass"), new Uri("http://localhost:8332"), Network.RegTest));
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<IBitcoinBlockchain>(), Is.TypeOf<RpcBlockchain>());
    }

    [Test]
    public void AddRpcBlockchain_GetUtxosAsync_ThrowsNotSupported()
    {
        // Bitcoin Core RPC has no address-indexed UTXO API; the RPC impl
        // honestly throws rather than silently returning empty.
        var services = new ServiceCollection();
        services.AddRpcBlockchain(new RPCClient(RPCCredentialString.Parse("user:pass"), new Uri("http://localhost:8332"), Network.RegTest));
        using var sp = services.BuildServiceProvider();

        var blockchain = sp.GetRequiredService<IBitcoinBlockchain>();
        Assert.ThrowsAsync<NotSupportedException>(async () =>
            await blockchain.GetUtxosAsync("bcrt1q...", CancellationToken.None));
    }

    [Test]
    public void Composites_LastRegistrationWins()
    {
        // ServiceCollection's standard semantics — the second AddSingleton
        // overrides the first. Lets a consumer call AddNBXplorerBlockchain
        // for the bulk wiring, then swap to Esplora for a specific scenario.
        var services = new ServiceCollection();
        services.AddNBXplorerBlockchain(NewExplorerClient());
        services.AddSingleton<IBitcoinBlockchain>(_ => new EsploraBlockchain(new Uri("http://localhost:30000")));
        using var sp = services.BuildServiceProvider();

        Assert.That(sp.GetService<IBitcoinBlockchain>(), Is.TypeOf<EsploraBlockchain>(),
            "Last AddSingleton registration should win");
    }
}
