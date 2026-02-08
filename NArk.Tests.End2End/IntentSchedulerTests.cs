using Microsoft.Extensions.Options;
using NArk.Abstractions.Intents;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;
using NBitcoin;

namespace NArk.Tests.End2End;

public class IntentSchedulerTests
{
    [Test]
    public async Task CanScheduleIntent()
    {
        var app = SharedArkInfrastructure.App;
        var walletDetails = await FundedWalletHelper.GetFundedWallet(app);
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, app.GetEndpoint("nbxplorer", "http"));
        // The threshold is so high, it will force an intent generation
        var scheduler = new SimpleIntentScheduler(new DefaultFeeEstimator(walletDetails.clientTransport, chainTimeProvider),
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
            // Verify IntentTxId is computed from RegisterProof PSBT's transaction hash
            var registerProofPsbt = PSBT.Parse(intent.RegisterProof, Network.RegTest);
            var expectedIntentTxId = registerProofPsbt.GetGlobalTransaction().GetHash().ToString();
            Assert.That(intent.IntentTxId, Is.EqualTo(expectedIntentTxId),
                "IntentTxId should match the RegisterProof PSBT's transaction hash");

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
            new DefaultFeeEstimator(walletDetails.clientTransport, chainTimeProvider), coinService, walletDetails.walletProvider, intentStorage, walletDetails.safetyService,
            walletDetails.contracts, walletDetails.vtxoStorage, scheduler,
            options);
        await using var intentSync = new IntentSynchronizationService(intentStorage, walletDetails.clientTransport, walletDetails.safetyService);
        await intentGeneration.StartAsync();
        await intentSync.StartAsync();

        await weGotNewIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));
        await weGotNewSubmittedIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }
}
