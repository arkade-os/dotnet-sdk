using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Wallets;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Blockchain;
using NArk.Core;
using NArk.Core.Events;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;

using NBitcoin;

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

    /// <summary>
    /// When a SpendingService offchain tx uses N input VTXOs, it must submit exactly N checkpoint
    /// transactions to arkd — one per input. This verifies the N-inputs → N-checkpoints invariant.
    /// </summary>
    [Test]
    public async Task MultiInputOffchainTx_ProducesOneCheckpointPerInput()
    {
        const int numInputs = 3;
        const long amountPerVtxo = 50_000;
        // Spend more than (numInputs-1) VTXOs can cover to force selection of all N inputs.
        const long spendAmount = (numInputs - 1) * amountPerVtxo + 1;

        var walletDetails = await FundedWalletHelper.GetFundedWallet(
            vtxoCount: numInputs, amountSatsPerVtxo: (int)amountPerVtxo);

        var coinService = new CoinService(walletDetails.clientTransport, walletDetails.contracts,
            [new PaymentContractTransformer(walletDetails.walletProvider),
             new HashLockedContractTransformer(walletDetails.walletProvider)]);

        var spyTransport = new CheckpointSpyTransport(walletDetails.clientTransport);

        var receiveContract = await walletDetails.contractService.DeriveContract(
            walletDetails.walletIdentifier, NextContractPurpose.Receive);

        var spendingService = new SpendingService(
            walletDetails.vtxoStorage, walletDetails.contracts, walletDetails.walletProvider,
            coinService, walletDetails.contractService, spyTransport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(),
            walletDetails.safetyService, TestStorage.CreateIntentStorage());

        await spendingService.Spend(walletDetails.walletIdentifier,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(spendAmount), receiveContract.GetArkAddress())
        ]);

        Assert.That(spyTransport.LastSubmitCheckpointCount, Is.EqualTo(numInputs),
            $"Offchain tx with {numInputs} input VTXOs must produce exactly {numInputs} checkpoint transactions");
    }

    // Decorator that records the checkpoint count passed to SubmitTx.
    // Must implement every IClientTransport method — if the interface gains a new member
    // this breaks at compile time, which is intentional (fail loudly, don't silently skip).
    private sealed class CheckpointSpyTransport(IClientTransport inner) : IClientTransport
    {
        public int LastSubmitCheckpointCount { get; private set; }

        public async Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs,
            CancellationToken cancellationToken = default)
        {
            LastSubmitCheckpointCount = checkpointTxs.Length;
            return await inner.SubmitTx(signedArkTx, checkpointTxs, cancellationToken);
        }

        public Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs, CancellationToken cancellationToken)
            => inner.FinalizeTx(arkTxId, finalCheckpointTxs, cancellationToken);
        public Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
            => inner.GetServerInfoAsync(cancellationToken);
        public IAsyncEnumerable<VtxoSubscriptionEvent> OpenSubscriptionStreamAsync(IReadOnlySet<string>? initialScripts, string? existingSubscriptionId, CancellationToken cancellationToken = default)
            => inner.OpenSubscriptionStreamAsync(initialScripts, existingSubscriptionId, cancellationToken);
        public Task UpdateSubscriptionScriptsAsync(string subscriptionId, IReadOnlySet<string>? add, IReadOnlySet<string>? remove, CancellationToken cancellationToken = default)
            => inner.UpdateSubscriptionScriptsAsync(subscriptionId, add, remove, cancellationToken);
        public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts, CancellationToken cancellationToken = default)
            => inner.GetVtxoByScriptsAsSnapshot(scripts, cancellationToken);
        public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts, DateTimeOffset? after, DateTimeOffset? before, CancellationToken cancellationToken = default)
            => inner.GetVtxoByScriptsAsSnapshot(scripts, after, before, cancellationToken);
        public IAsyncEnumerable<ArkVtxo> GetVtxosByOutpoints(IReadOnlyCollection<OutPoint> outpoints, bool spentOnly = false, CancellationToken cancellationToken = default)
            => inner.GetVtxosByOutpoints(outpoints, spentOnly, cancellationToken);
        public Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default)
            => inner.RegisterIntent(intent, cancellationToken);
        public Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default)
            => inner.DeleteIntent(intent, cancellationToken);
        public Task SubmitTreeNoncesAsync(SubmitTreeNoncesRequest treeNonces, CancellationToken cancellationToken)
            => inner.SubmitTreeNoncesAsync(treeNonces, cancellationToken);
        public Task SubmitTreeSignaturesRequest(SubmitTreeSignaturesRequest treeSigs, CancellationToken cancellationToken)
            => inner.SubmitTreeSignaturesRequest(treeSigs, cancellationToken);
        public Task SubmitSignedForfeitTxsAsync(SubmitSignedForfeitTxsRequest req, CancellationToken cancellationToken)
            => inner.SubmitSignedForfeitTxsAsync(req, cancellationToken);
        public Task ConfirmRegistrationAsync(string intentId, CancellationToken cancellationToken)
            => inner.ConfirmRegistrationAsync(intentId, cancellationToken);
        public IAsyncEnumerable<BatchEvent> GetEventStreamAsync(GetEventStreamRequest req, CancellationToken cancellationToken)
            => inner.GetEventStreamAsync(req, cancellationToken);
        public Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
            => inner.GetAssetDetailsAsync(assetId, cancellationToken);
        public Task UpdateStreamTopicsAsync(string streamId, string[]? addTopics, string[]? removeTopics, CancellationToken cancellationToken = default)
            => inner.UpdateStreamTopicsAsync(streamId, addTopics, removeTopics, cancellationToken);
        public Task<ArkIntent[]> GetIntentsByProofAsync(string proof, string message, CancellationToken cancellationToken = default)
            => inner.GetIntentsByProofAsync(proof, message, cancellationToken);
        public Task<PendingArkTransaction[]> GetPendingTxAsync(string proof, string message, CancellationToken cancellationToken = default)
            => inner.GetPendingTxAsync(proof, message, cancellationToken);
        public Task<IReadOnlyList<VtxoChainEntry>> GetVtxoChainAsync(OutPoint vtxoOutpoint, string? intentProof = null, string? intentMessage = null, CancellationToken cancellationToken = default)
            => inner.GetVtxoChainAsync(vtxoOutpoint, intentProof, intentMessage, cancellationToken);
        public Task<IReadOnlyList<string>> GetVirtualTxsAsync(IReadOnlyList<string> txids, CancellationToken cancellationToken = default)
            => inner.GetVirtualTxsAsync(txids, cancellationToken);
        public Task<IReadOnlyList<VtxoTreeNode>> GetVtxoTreeAsync(OutPoint batchOutpoint, CancellationToken cancellationToken = default)
            => inner.GetVtxoTreeAsync(batchOutpoint, cancellationToken);
    }
}
