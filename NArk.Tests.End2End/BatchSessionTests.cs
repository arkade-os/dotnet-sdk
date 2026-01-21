using Aspire.Hosting;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Intents;
using NArk.Blockchain.NBXplorer;
using NArk.Fees;
using NArk.Models.Options;
using NArk.Services;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transformers;
using NBitcoin;

namespace NArk.Tests.End2End;

public class BatchSessionTests
{
    private DistributedApplication _app;

    [OneTimeSetUp]
    public async Task StartDependencies()
    {
        ThreadPool.SetMinThreads(50, 50);

        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: ["--noswap"],
                configureBuilder: (appOptions, _) => { appOptions.AllowUnsecuredTransport = true; }
            );

        // Start dependencies
        _app = await builder.BuildAsync();
        await _app.StartAsync(CancellationToken.None);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("ark", CancellationToken.None);
    }

    [OneTimeTearDown]
    public async Task StopDependencies()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Test]
    public async Task CanDoFullBatchSessionUsingGeneratedIntent()
    {
        var walletDetails = await FundedWalletHelper.GetFundedWallet(_app);

        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http"));
        var coinService = new CoinService(walletDetails.clientTransport, walletDetails.contracts,
            [new PaymentContractTransformer(walletDetails.walletProvider), new HashLockedContractTransformer(walletDetails.walletProvider)]);

        var intentStorage = new InMemoryIntentStorage();

        // The threshold is so high, it will force an intent generation
        var scheduler = new SimpleIntentScheduler(new DefaultFeeEstimator(walletDetails.clientTransport), walletDetails.clientTransport, walletDetails.contractService,
            new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http")),
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions()
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));

        var newIntentTcs = new TaskCompletionSource();
        var newSubmittedIntentTcs = new TaskCompletionSource();
        var newSuccessBatch = new TaskCompletionSource();

        intentStorage.IntentChanged += (_, intent) =>
        {
            switch (intent.State)
            {
                case ArkIntentState.WaitingToSubmit:
                    newIntentTcs.TrySetResult();
                    break;
                case ArkIntentState.WaitingForBatch:
                    newSubmittedIntentTcs.TrySetResult();
                    break;
                case ArkIntentState.BatchSucceeded:
                    newSuccessBatch.TrySetResult();
                    break;
            }
        };

        var intentGenerationOptions = new OptionsWrapper<IntentGenerationServiceOptions>(new IntentGenerationServiceOptions()
        { PollInterval = TimeSpan.FromHours(5) });


        await using var intentGeneration = new IntentGenerationService(walletDetails.clientTransport,
            new DefaultFeeEstimator(walletDetails.clientTransport),
            coinService,
            walletDetails.walletProvider,
            intentStorage,
            walletDetails.safetyService,
            walletDetails.contracts, walletDetails.vtxoStorage, scheduler,
            intentGenerationOptions);
        await intentGeneration.StartAsync(CancellationToken.None);
        await newIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));


        await using var intentSync =
            new IntentSynchronizationService(intentStorage, walletDetails.clientTransport, walletDetails.safetyService);
        await intentSync.StartAsync(CancellationToken.None);

        await newSubmittedIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));

        await using var batchManager = new BatchManagementService(intentStorage,
            walletDetails.clientTransport, walletDetails.vtxoStorage,
            walletDetails.walletProvider, coinService, walletDetails.safetyService);

        await batchManager.StartAsync(CancellationToken.None);

        await newSuccessBatch.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }
}