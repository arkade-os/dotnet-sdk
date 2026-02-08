using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Hosting;
using NArk.Core.Models.Options;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Services;
using NArk.Abstractions.Extensions;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Core;

public class OnchainTests
{
    [Test]
    public async Task CanParticipateInBatchWithColabExit()
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
                .WithVtxoStorage<InMemoryVtxoStorage>()
                .WithWalletProvider<InMemoryWalletProvider>()
                .WithTimeProvider<ChainTimeProvider>()
                .ConfigureServices(s => s.Configure<ChainTimeProviderOptions>(o =>
                {
                    o.Network = Network.RegTest;
                    o.Uri = app.GetEndpoint("nbxplorer", "http");
                }))
                // Prevent usual intents from getting in the way
                .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
                {
                    o.Threshold = TimeSpan.FromSeconds(2);
                    o.ThresholdHeight = 1;
                }))
                .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(o =>
                    o.PollInterval = TimeSpan.FromSeconds(5)))
                .Build();

        await arkHost.StartAsync();

        var contractService = arkHost.Services.GetRequiredService<IContractService>();
        var wallet = arkHost.Services.GetRequiredService<InMemoryWalletProvider>();
        var intentStorage = arkHost.Services.GetRequiredService<IIntentStorage>();

        var fp1 = await wallet.CreateTestWallet();
        var fp2 = await wallet.CreateTestWallet();
        var contract = await contractService.DeriveContract(fp1, NextContractPurpose.Receive, cancellationToken: CancellationToken.None);

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                "50000", "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        var destination =
            new TaprootAddress(
                new TaprootPubKey((await ((await wallet.GetAddressProviderAsync(fp2))!).GetNextSigningDescriptor()).Extract().XOnlyPubKey!.ToBytes()), Network.RegTest);

        var onchainService = arkHost.Services.GetRequiredService<IOnchainService>();
        await onchainService.InitiateCollaborativeExit(
            fp1,
            new ArkTxOut(
                ArkTxOutType.Onchain,
                10000UL,
                destination
            ),
            CancellationToken.None
        );

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