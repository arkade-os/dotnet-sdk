using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Intents;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Contracts;
using NArk.Hosting;
using NArk.Core.Models.Options;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Services;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Core;

public class NoteTests
{
    [Test]
    public async Task CanCompleteBatchWithOnlyOneNote()
    {
        var arkHost =
            Host.CreateDefaultBuilder([])
            .AddArk()
            .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
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
                o.Uri = SharedArkInfrastructure.NbxplorerEndpoint;
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

        var note = await DockerHelper.CreateArkNote();

        if (string.IsNullOrEmpty(note))
            throw new Exception("Note creation failed!");

        var fp = await wallet.CreateTestWallet();

        await contractService.ImportContract(fp, ArkNoteContract.Parse(note));

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