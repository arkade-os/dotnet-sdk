using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Hosting;
using NArk.Core.Models.Options;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Services;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End;

public class BuilderStyleTests
{
    [Test]
    public async Task CanParticipateInBatchSessionBuilderStyle()
    {
        var app = SharedArkInfrastructure.App;
        var arkHost =
            Host.CreateDefaultBuilder([])
            .AddArk()
            .OnCustomGrpcArk(app.GetEndpoint("ark", "arkd").ToString())
            .WithSafetyService<AsyncSafetyService>()
            .WithIntentStorage<InMemoryIntentStorage>()
            .WithIntentScheduler<SimpleIntentScheduler>()
            .WithSwapStorage<InMemorySwapStorage>()
            .WithContractStorage<InMemoryContractStorage>()
            .WithWalletProvider<InMemoryWalletProvider>()
            .WithVtxoStorage<InMemoryVtxoStorage>()
            .WithTimeProvider<ChainTimeProvider>()
            .ConfigureServices(s => s.Configure<ChainTimeProviderOptions>(o =>
            {
                o.Network = Network.RegTest;
                o.Uri = app.GetEndpoint("nbxplorer", "http");
            }))
            .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
            {
                o.Threshold = TimeSpan.FromHours(2);
                o.ThresholdHeight = 2000;
            }))
            .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(o => o.PollInterval = TimeSpan.FromSeconds(5)))
            .Build();

        await arkHost.StartAsync();

        var contractService = arkHost.Services.GetRequiredService<IContractService>();
        var wallet = arkHost.Services.GetRequiredService<InMemoryWalletProvider>();
        var intentStorage = arkHost.Services.GetRequiredService<IIntentStorage>();

        var fp = await wallet.CreateTestWallet();
        var contract = await contractService.DeriveContract(fp, NextContractPurpose.Receive, cancellationToken: CancellationToken.None);

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                "50000", "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        var gotBatchTcs = new TaskCompletionSource();

        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                gotBatchTcs.TrySetResult();
        };

        await gotBatchTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));

        await arkHost.StopAsync();
    }

}