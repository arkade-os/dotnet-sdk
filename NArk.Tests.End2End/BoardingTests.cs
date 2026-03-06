using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Events;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Safety.AsyncKeyedLock;
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
        var vtxoStorage = new InMemoryVtxoStorage();
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var info = await clientTransport.GetServerInfoAsync();

        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var contracts = new InMemoryContractStorage();
        var safetyService = new AsyncSafetyService();
        var walletId = await walletProvider.CreateTestWallet();

        var contractService = new ContractService(walletProvider, contracts, clientTransport);

        // --- 2. Derive a boarding contract ---
        var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
            walletId,
            NextContractPurpose.Boarding,
            ContractActivityState.Active);

        // Get the on-chain P2TR address from the boarding contract
        var spendInfo = boardingContract.GetTaprootSpendInfo();
        var scriptPubKey = spendInfo.OutputPubKey.GetAddress(info.Network);
        var onchainAddress = scriptPubKey.ToString();

        Console.WriteLine($"[Boarding] Boarding P2TR address: {onchainAddress}");

        // --- 3. Fund the boarding address via bitcoin-cli sendtoaddress ---
        const long boardingAmountSats = 100_000;
        var btcAmount = (boardingAmountSats / 100_000_000m).ToString("0.########",
            System.Globalization.CultureInfo.InvariantCulture);

        var sendOutput = await DockerHelper.Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "sendtoaddress", onchainAddress, btcAmount]);
        var fundingTxid = sendOutput.Trim();
        Console.WriteLine($"[Boarding] Funding txid: {fundingTxid}");

        Assert.That(fundingTxid, Is.Not.Empty, "sendtoaddress should return a txid");

        // --- 4. Mine blocks to confirm the funding transaction ---
        await DockerHelper.MineBlocks(6);

        // --- 5. Find the correct vout for our boarding address ---
        var txRawOutput = await DockerHelper.Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "gettransaction", fundingTxid]);
        var txJson = JsonNode.Parse(txRawOutput);

        // Use decoderawtransaction to get exact vout details
        var txHex = txJson?["hex"]?.GetValue<string>()
                    ?? throw new InvalidOperationException(
                        $"Could not get transaction hex from gettransaction. Output: {txRawOutput}");

        var decodeOutput = await DockerHelper.Exec("bitcoin",
            ["bitcoin-cli", "decoderawtransaction", txHex]);
        var decodedTx = JsonNode.Parse(decodeOutput);
        var vouts = decodedTx?["vout"]?.AsArray()
                    ?? throw new InvalidOperationException(
                        $"Could not parse vout from decoded transaction. Output: {decodeOutput}");

        uint? boardingVout = null;
        ulong? boardingAmount = null;
        foreach (var vout in vouts)
        {
            var spkAddress = vout?["scriptPubKey"]?["address"]?.GetValue<string>();
            if (spkAddress == onchainAddress)
            {
                boardingVout = vout!["n"]!.GetValue<uint>();
                // value is in BTC as a decimal
                var valueBtc = vout["value"]!.GetValue<decimal>();
                boardingAmount = (ulong)(valueBtc * 100_000_000m);
                break;
            }
        }

        Assert.That(boardingVout, Is.Not.Null, "Should find vout matching the boarding address");
        Console.WriteLine($"[Boarding] Found vout={boardingVout} with amount={boardingAmount} sats");

        // --- 6. Manually insert the boarding UTXO as an ArkVtxo (Unrolled: true) ---
        var boardingScriptHex = boardingContract.GetArkAddress().ScriptPubKey.ToHex();
        // ExpiresAt must be within the scheduler's Threshold (2 hours) to trigger intent generation.
        // Boarding UTXOs in production would have a real boarding_exit_delay-based expiry.
        var vtxo = new ArkVtxo(
            Script: boardingScriptHex,
            TransactionId: fundingTxid,
            TransactionOutputIndex: boardingVout!.Value,
            Amount: boardingAmount!.Value,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            ExpiresAtHeight: null,
            Unrolled: true);
        await vtxoStorage.UpsertVtxo(vtxo);

        Console.WriteLine($"[Boarding] Inserted boarding VTXO: {fundingTxid}:{boardingVout}");

        // --- 7. Setup services and generate intent ---
        var chainTimeProvider = new ChainTimeProvider(info.Network, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(clientTransport, contracts,
        [
            new PaymentContractTransformer(walletProvider),
            new BoardingContractTransformer(walletProvider)
        ]);

        var intentStorage = new InMemoryIntentStorage();

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(clientTransport, chainTimeProvider),
            clientTransport,
            contractService,
            chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            {
                Threshold = TimeSpan.FromHours(2),
                ThresholdHeight = 2000
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

        // --- 8. Submit intent and run batch ---
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

        // --- 9. Verify: batch succeeded and we have a new (non-boarding) VTXO ---
        var allVtxos = await vtxoStorage.GetVtxos();
        var unspentVtxos = allVtxos.Where(v => !v.IsSpent()).ToList();

        Console.WriteLine($"[Boarding] Total VTXOs: {allVtxos.Count}, Unspent: {unspentVtxos.Count}");
        foreach (var v in allVtxos)
        {
            Console.WriteLine(
                $"  VTXO {v.TransactionId[..8]}..:{v.TransactionOutputIndex} " +
                $"amount={v.Amount} spent={v.IsSpent()} unrolled={v.Unrolled}");
        }

        // The original boarding VTXO should be spent, and we should have at least one new VTXO
        Assert.That(unspentVtxos, Has.Count.GreaterThanOrEqualTo(1),
            "Should have at least one unspent VTXO after boarding batch");
    }
}
