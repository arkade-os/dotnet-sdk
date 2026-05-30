using System.Text;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Intents;
using NArk.Blockchain;
using NArk.Core.Events;
using NArk.Core.Extensions;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;

using NBitcoin;
using NBitcoin.Crypto;

namespace NArk.Tests.End2End.Core;

public class BatchSessionTests
{
    [Test]
    public async Task CanDoFullBatchSessionUsingGeneratedIntent()
    {
        var walletDetails = await FundedWalletHelper.GetFundedWallet();

        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(walletDetails.clientTransport, walletDetails.contracts,
            [new PaymentContractTransformer(walletDetails.walletProvider), new HashLockedContractTransformer(walletDetails.walletProvider)]);

        var intentStorage = TestStorage.CreateIntentStorage();

        // The threshold is so high, it will force an intent generation
        var scheduler = new SimpleIntentScheduler(new DefaultFeeEstimator(walletDetails.clientTransport, chainTimeProvider), walletDetails.clientTransport, walletDetails.contractService,
            chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions()
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));

        var newIntentTcs = new TaskCompletionSource();
        var newSubmittedIntentTcs = new TaskCompletionSource();
        var newSuccessBatch = new TaskCompletionSource();

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
            new DefaultFeeEstimator(walletDetails.clientTransport, chainTimeProvider),
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
            walletDetails.clientTransport, walletDetails.vtxoStorage, walletDetails.contracts,
            walletDetails.walletProvider, coinService, walletDetails.safetyService,
            Array.Empty<IEventHandler<PostBatchSessionEvent>>());

        await batchManager.StartAsync(CancellationToken.None);

        await newSuccessBatch.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// When two wallets submit intents concurrently, Arkade batches them together and
    /// returns the same commitment txid to both. This verifies the batch-sharing property:
    /// multiple participants in the same batch share an identical commitment transaction.
    /// </summary>
    [Test]
    public async Task TwoWallets_ConcurrentSettle_HaveIdenticalCommitmentTxId()
    {
        var alice = await FundedWalletHelper.GetFundedWallet(amountSatsPerVtxo: 500_000);
        var bob = await FundedWalletHelper.GetFundedWallet(amountSatsPerVtxo: 500_000);

        var aliceIntentStorage = TestStorage.CreateIntentStorage();
        var bobIntentStorage = TestStorage.CreateIntentStorage();

        string? aliceCommitmentTx = null;
        string? bobCommitmentTx = null;
        var aliceBatchTcs = new TaskCompletionSource();
        var bobBatchTcs = new TaskCompletionSource();

        aliceIntentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
            {
                aliceCommitmentTx = intent.CommitmentTransactionId;
                aliceBatchTcs.TrySetResult();
            }
        };
        bobIntentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
            {
                bobCommitmentTx = intent.CommitmentTransactionId;
                bobBatchTcs.TrySetResult();
            }
        };

        var chainTime = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var schedulerOptions = new OptionsWrapper<SimpleIntentSchedulerOptions>(
            new SimpleIntentSchedulerOptions { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 });
        var intentGenOptions = new OptionsWrapper<IntentGenerationServiceOptions>(
            new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromHours(5) });
        var noEventHandlers = Array.Empty<IEventHandler<PostBatchSessionEvent>>();

        var aliceCoinService = new CoinService(alice.clientTransport, alice.contracts,
            [new PaymentContractTransformer(alice.walletProvider), new HashLockedContractTransformer(alice.walletProvider)]);
        var bobCoinService = new CoinService(bob.clientTransport, bob.contracts,
            [new PaymentContractTransformer(bob.walletProvider), new HashLockedContractTransformer(bob.walletProvider)]);

        await using var aliceIntentGen = new IntentGenerationService(
            alice.clientTransport, new DefaultFeeEstimator(alice.clientTransport, chainTime),
            aliceCoinService, alice.walletProvider, aliceIntentStorage, alice.safetyService,
            alice.contracts, alice.vtxoStorage,
            new SimpleIntentScheduler(new DefaultFeeEstimator(alice.clientTransport, chainTime),
                alice.clientTransport, alice.contractService, chainTime, schedulerOptions),
            intentGenOptions);

        await using var bobIntentGen = new IntentGenerationService(
            bob.clientTransport, new DefaultFeeEstimator(bob.clientTransport, chainTime),
            bobCoinService, bob.walletProvider, bobIntentStorage, bob.safetyService,
            bob.contracts, bob.vtxoStorage,
            new SimpleIntentScheduler(new DefaultFeeEstimator(bob.clientTransport, chainTime),
                bob.clientTransport, bob.contractService, chainTime, schedulerOptions),
            intentGenOptions);

        var aliceIntentReadyTcs = new TaskCompletionSource();
        var bobIntentReadyTcs = new TaskCompletionSource();
        aliceIntentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.WaitingToSubmit) aliceIntentReadyTcs.TrySetResult();
        };
        bobIntentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.WaitingToSubmit) bobIntentReadyTcs.TrySetResult();
        };

        await Task.WhenAll(
            aliceIntentGen.StartAsync(CancellationToken.None),
            bobIntentGen.StartAsync(CancellationToken.None));

        await Task.WhenAll(
            aliceIntentReadyTcs.Task.WaitAsync(TimeSpan.FromMinutes(1)),
            bobIntentReadyTcs.Task.WaitAsync(TimeSpan.FromMinutes(1)));

        var aliceRegisteredTcs = new TaskCompletionSource();
        var bobRegisteredTcs = new TaskCompletionSource();
        aliceIntentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.WaitingForBatch) aliceRegisteredTcs.TrySetResult();
        };
        bobIntentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.WaitingForBatch) bobRegisteredTcs.TrySetResult();
        };

        await using var aliceIntentSync = new IntentSynchronizationService(
            aliceIntentStorage, alice.clientTransport, alice.safetyService);
        await using var bobIntentSync = new IntentSynchronizationService(
            bobIntentStorage, bob.clientTransport, bob.safetyService);

        await using var aliceBatchMgr = new BatchManagementService(aliceIntentStorage,
            alice.clientTransport, alice.vtxoStorage, alice.contracts,
            alice.walletProvider, aliceCoinService, alice.safetyService, noEventHandlers);
        await using var bobBatchMgr = new BatchManagementService(bobIntentStorage,
            bob.clientTransport, bob.vtxoStorage, bob.contracts,
            bob.walletProvider, bobCoinService, bob.safetyService, noEventHandlers);

        await Task.WhenAll(
            aliceIntentSync.StartAsync(CancellationToken.None),
            aliceBatchMgr.StartAsync(CancellationToken.None),
            bobIntentSync.StartAsync(CancellationToken.None),
            bobBatchMgr.StartAsync(CancellationToken.None));

        await Task.WhenAll(
            aliceRegisteredTcs.Task.WaitAsync(TimeSpan.FromMinutes(1)),
            bobRegisteredTcs.Task.WaitAsync(TimeSpan.FromMinutes(1)));

        await Task.WhenAll(
            aliceBatchTcs.Task.WaitAsync(TimeSpan.FromMinutes(2)),
            bobBatchTcs.Task.WaitAsync(TimeSpan.FromMinutes(2)));

        Assert.That(aliceCommitmentTx, Is.Not.Null.And.Not.Empty);
        Assert.That(aliceCommitmentTx, Is.EqualTo(bobCommitmentTx),
            "Both wallets must share the same commitment txid when they settle in the same Arkade batch");
    }
}