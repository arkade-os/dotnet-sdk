using Microsoft.Extensions.Options;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Events;
using NArk.Blockchain;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Abstractions.Safety;
using NArk.Safety.AsyncKeyedLock;
using NArk.Tests.Common;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;
using NBitcoin;

namespace NArk.Tests.End2End.Core;

public class BoardingTests
{
    [Test]
    public async Task CanBoardFromOnchainToVtxo()
    {
        // --- 1. Setup wallet and transport ---
        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);
        var vtxoStorage = storage.VtxoStorage;
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var info = await clientTransport.GetServerInfoAsync();

        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var contracts = storage.ContractStorage;
        var walletId = await walletProvider.CreateTestWallet();

        var contractService = new ContractService(walletProvider, contracts, clientTransport);

        // --- 2. Derive a boarding contract ---
        var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
            walletId,
            NextContractPurpose.Boarding,
            ContractActivityState.Active);

        var onchainAddress = boardingContract.GetOnchainAddress(info.Network).ToString();
        Console.WriteLine($"[Boarding] Boarding P2TR address: {onchainAddress}");

        // --- 3. Fund the boarding address via bitcoin-cli ---
        const long boardingAmountSats = 100_000;

        var fundingTxid = await DockerHelper.BitcoinSendToAddress(onchainAddress, Money.Satoshis(boardingAmountSats));
        Console.WriteLine($"[Boarding] Funding txid: {fundingTxid}");
        Assert.That(fundingTxid, Is.Not.Empty, "sendtoaddress should return a txid");

        // --- 4. Mine blocks to confirm ---
        await DockerHelper.MineBlocks(6);

        // --- 5. Sync boarding UTXOs from Esplora (Chopsticks) ---
        var utxoProvider = new EsploraBlockchain(SharedArkInfrastructure.ChopsticksEndpoint);
        var syncService = new BoardingUtxoSyncService(
            contracts, vtxoStorage, clientTransport, utxoProvider);

        // The Esplora backend may need a moment to index the just-mined blocks —
        // retry until the UTXO appears *confirmed* (ExpiresAt set). A row synced
        // while the funding tx still looked unconfirmed has ExpiresAt=null, which
        // SimpleIntentScheduler silently filters out (arkd rejects unconfirmed
        // inputs); since the generation cycle below runs only once per
        // PollInterval, that would stall the whole test. SyncAsync re-reads
        // Esplora and upserts each iteration, so the row flips to confirmed as
        // soon as the indexer catches up.
        ArkVtxo? syncedVtxo = null;
        for (var i = 0; i < 10; i++)
        {
            await syncService.SyncAsync();
            var vtxos = await vtxoStorage.GetVtxos();
            syncedVtxo = vtxos.FirstOrDefault(v => v.TransactionId == fundingTxid && v.ExpiresAt is not null);
            if (syncedVtxo is not null)
                break;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.That(syncedVtxo, Is.Not.Null,
            "BoardingUtxoSyncService should find the funded UTXO via Esplora as confirmed (ExpiresAt set)");
        Assert.That(syncedVtxo!.Unrolled, Is.True);
        Assert.That(syncedVtxo.Amount, Is.EqualTo((ulong)boardingAmountSats));
        Console.WriteLine($"[Boarding] Synced VTXO: {syncedVtxo.TransactionId[..8]}..:{syncedVtxo.TransactionOutputIndex}");

        // --- 6. Setup services and generate intent ---
        var chainTimeProvider = new NBXplorerBlockchain(info.Network, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(clientTransport, contracts,
        [
            new PaymentContractTransformer(walletProvider),
            new BoardingContractTransformer(walletProvider)
        ]);

        var intentStorage = storage.IntentStorage;

        // ThresholdHeight must cover the boarding exit delay (144 blocks) so the
        // scheduler picks up boarding VTXOs whose ExpiresAtHeight is ~144 blocks away.
        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(clientTransport, chainTimeProvider),
            clientTransport,
            contractService,
            chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            {
                Threshold = TimeSpan.FromHours(25),
                ThresholdHeight = 200
            }));

        var newIntentTcs = new TaskCompletionSource();
        var newSubmittedIntentTcs = new TaskCompletionSource();
        var newSuccessBatch = new TaskCompletionSource();

        intentStorage.IntentChanged += (_, intent) =>
        {
            Console.WriteLine($"[Boarding] Intent state changed: {intent.State}");
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
                // Surface terminal failures immediately instead of letting the
                // staged waits below time out blind — the recorded reason makes
                // intermittent failures attributable from CI output alone.
                case ArkIntentState.BatchFailed:
                case ArkIntentState.Cancelled:
                    var failure = new InvalidOperationException(
                        $"Intent {intent.IntentTxId} ended in {intent.State}: {intent.CancellationReason ?? "no reason recorded"}");
                    newIntentTcs.TrySetException(failure);
                    newSubmittedIntentTcs.TrySetException(failure);
                    newSuccessBatch.TrySetException(failure);
                    break;
            }
        };

        var intentGenerationOptions = new OptionsWrapper<IntentGenerationServiceOptions>(
            new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromHours(5) });

        await using var intentGeneration = new IntentGenerationService(
            clientTransport,
            new DefaultFeeEstimator(clientTransport, chainTimeProvider),
            coinService,
            walletProvider,
            intentStorage,
            safetyService,
            contracts,
            vtxoStorage,
            scheduler,
            intentGenerationOptions);
        await intentGeneration.StartAsync(CancellationToken.None);
        await newIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));
        Console.WriteLine("[Boarding] Intent generated");

        // --- 7. Submit intent and run batch ---
        await using var intentSync =
            new IntentSynchronizationService(intentStorage, clientTransport, safetyService);
        await intentSync.StartAsync(CancellationToken.None);
        await newSubmittedIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));
        Console.WriteLine("[Boarding] Intent submitted");

        await using var batchManager = new BatchManagementService(
            intentStorage,
            clientTransport,
            vtxoStorage,
            contracts,
            walletProvider,
            coinService,
            safetyService,
            Array.Empty<IEventHandler<PostBatchSessionEvent>>());
        await batchManager.StartAsync(CancellationToken.None);
        await newSuccessBatch.Task.WaitAsync(TimeSpan.FromMinutes(2));
        Console.WriteLine("[Boarding] Batch succeeded");

        // --- 8. Verify: batch succeeded and we have a new (non-boarding) VTXO ---
        var allVtxos = await vtxoStorage.GetVtxos();
        var unspentVtxos = allVtxos.Where(v => !v.IsSpent()).ToList();

        Console.WriteLine($"[Boarding] Total VTXOs: {allVtxos.Count}, Unspent: {unspentVtxos.Count}");
        foreach (var v in allVtxos)
        {
            Console.WriteLine(
                $"  VTXO {v.TransactionId[..8]}..:{v.TransactionOutputIndex} " +
                $"amount={v.Amount} spent={v.IsSpent()} unrolled={v.Unrolled}");
        }

        Assert.That(unspentVtxos, Has.Count.GreaterThanOrEqualTo(1),
            "Should have at least one unspent VTXO after boarding batch");
    }
}
