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

public class IntentSchedulerTests
{
    private DistributedApplication _app;

    [OneTimeSetUp]
    public async Task StartDependencies()
    {
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
    public async Task CanScheduleIntent()
    {
        var walletDetails = await FundedWalletHelper.GetFundedWallet(_app);
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http"));
        // The threshold is so high, it will force an intent generation
        var scheduler = new SimpleIntentScheduler(new DefaultFeeEstimator(walletDetails.clientTransport),
            walletDetails.clientTransport, walletDetails.contractService, chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions()
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));

        var intentStorage = new InMemoryIntentStorage();

        var options =
            new OptionsWrapper<IntentGenerationServiceOptions>(
                new IntentGenerationServiceOptions() { PollInterval = TimeSpan.FromMinutes(5) }
            );

        var weGotNewIntentTcs = new TaskCompletionSource();
        var weGotNewSubmittedIntentTcs = new TaskCompletionSource();

        intentStorage.IntentChanged += (_, intent) =>
        {
            switch (intent.State)
            {
                case ArkIntentState.WaitingToSubmit:
                    weGotNewIntentTcs.TrySetResult();
                    break;
                case ArkIntentState.WaitingForBatch:
                    weGotNewSubmittedIntentTcs.TrySetResult();
                    break;
            }
        };

        var coinService = new CoinService(walletDetails.clientTransport, walletDetails.contracts,
            [new PaymentContractTransformer(walletDetails.walletProvider), new HashLockedContractTransformer(walletDetails.walletProvider)]);
        await using var intentGeneration = new IntentGenerationService(walletDetails.clientTransport,
            new DefaultFeeEstimator(walletDetails.clientTransport), coinService, walletDetails.walletProvider, intentStorage, walletDetails.safetyService,
            walletDetails.contracts, walletDetails.vtxoStorage, scheduler,
            options);
        await using var intentSync = new IntentSynchronizationService(intentStorage, walletDetails.clientTransport, walletDetails.safetyService);
        await intentGeneration.StartAsync();
        await intentSync.StartAsync();

        await weGotNewIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));
        await weGotNewSubmittedIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }
}