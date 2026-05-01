using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Blockchain.NBXplorer;
using NArk.Core;
using NArk.Core.Extensions;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End;

public class ArkCashTests
{
    [Test]
    public async Task RoundripsCorrectly()
    {
        var walletDetails = await FundedWalletHelper.GetFundedWallet();
        var serverInfo = await walletDetails.clientTransport.GetServerInfoAsync();

        // create arkcash
        var cash = await CreateFundedArkCash(serverInfo, 100000);
        
        // import arkcash contract to receiver wallet
        var receiverWalletId = await walletDetails.walletProvider.CreateTestWallet();
        await walletDetails.contractService.ImportContract(receiverWalletId, cash.ToContract(serverInfo.Network));

        // start services needed to participate in a batch (same pattern as BatchSessionTests)
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(walletDetails.clientTransport, walletDetails.contracts,
            [new PaymentContractTransformer(walletDetails.walletProvider), new HashLockedContractTransformer(walletDetails.walletProvider)]);
        var intentStorage = TestStorage.CreateIntentStorage();
        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(walletDetails.clientTransport, chainTimeProvider),
            walletDetails.clientTransport,
            walletDetails.contractService,
            chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
                { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));

        var gotBatchTcs = new TaskCompletionSource();

        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                gotBatchTcs.TrySetResult();
        };

        await using var intentGeneration = new IntentGenerationService(
            walletDetails.clientTransport,
            new DefaultFeeEstimator(walletDetails.clientTransport, chainTimeProvider),
            coinService,
            walletDetails.walletProvider,
            intentStorage,
            walletDetails.safetyService,
            walletDetails.contracts,
            walletDetails.vtxoStorage,
            scheduler,
            new OptionsWrapper<IntentGenerationServiceOptions>(new IntentGenerationServiceOptions
                { PollInterval = TimeSpan.FromSeconds(5) }));
        await intentGeneration.StartAsync(CancellationToken.None);

        await using var intentSync = new IntentSynchronizationService(
            intentStorage, walletDetails.clientTransport, walletDetails.safetyService);
        await intentSync.StartAsync(CancellationToken.None);

        await using var batchManager = new BatchManagementService(
            intentStorage,
            walletDetails.clientTransport,
            walletDetails.vtxoStorage,
            walletDetails.contracts,
            walletDetails.walletProvider,
            coinService,
            walletDetails.safetyService);
        await batchManager.StartAsync(CancellationToken.None);

        await gotBatchTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));

        var cashScript = cash.GetAddress(serverInfo.Network).ScriptPubKey.ToHex();
        await WaitForArkCashClaimed(walletDetails.vtxoStorage, cashScript, receiverWalletId, TimeSpan.FromSeconds(30));

        var oldUnspent = await walletDetails.vtxoStorage.GetVtxos(
            scripts: [cashScript],
            includeSpent: false);
        var receiverUnspent = await walletDetails.vtxoStorage.GetVtxos(
            walletIds: [receiverWalletId],
            includeSpent: false);

        Assert.That(oldUnspent.Count, Is.EqualTo(0), "ArkCash script should be fully spent after claim");
        Assert.That(receiverUnspent.Count, Is.GreaterThan(0), "Receiver should have at least one unspent VTXO after claim");
    }
    

    private async Task<ArkCash> CreateFundedArkCash(ArkServerInfo serverInfo, ulong amount)
    {
        
        //create testnet arkcash
        var cash = ArkCash.Generate(
            serverInfo.SignerKey.ToXOnlyPubKey(), 
            serverInfo.UnilateralExit, 
            "tarkcash");
        
        //fund arkcash
        var cashAddress = cash.GetAddress(serverInfo.Network);
        await Cli.Wrap("docker")
            .WithArguments(["exec", "ark", "ark", "send",
                "--to", cashAddress.ToString(false),
                "--amount", amount.ToString(),
                "--password", "secret"])
            .ExecuteBufferedAsync();
        
        return cash;
    }

    private static async Task WaitForArkCashClaimed(
        NArk.Abstractions.VTXOs.IVtxoStorage vtxoStorage,
        string cashScript,
        string receiverWalletId,
        TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> IsClaimCompleted()
        {
            var oldUnspent = await vtxoStorage.GetVtxos(
                scripts: [cashScript],
                includeSpent: false);
            var receiverUnspent = await vtxoStorage.GetVtxos(
                walletIds: [receiverWalletId],
                includeSpent: false);

            return oldUnspent.Count == 0 && receiverUnspent.Count > 0;
        }

        async void Handler(object? _, NArk.Abstractions.VTXOs.ArkVtxo __)
        {
            if (await IsClaimCompleted())
                tcs.TrySetResult();
        }

        vtxoStorage.VtxosChanged += Handler;
        try
        {
            if (await IsClaimCompleted())
                return;

            await tcs.Task.WaitAsync(timeout);
        }
        finally
        {
            vtxoStorage.VtxosChanged -= Handler;
        }
    }
}
