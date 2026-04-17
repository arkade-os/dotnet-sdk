using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Blockchain.NBXplorer;
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
        var cash = ArkCash.Generate(serverInfo.SignerKey.ToXOnlyPubKey(), serverInfo.UnilateralExit);
        var cashAddress = cash.GetAddress(serverInfo.Network);

        // fund the arkcash
        await Cli.Wrap("docker")
            .WithArguments(["exec", "ark", "ark", "send",
                "--to", cashAddress.ToString(false),
                "--amount", "100000",
                "--password", "secret"])
            .ExecuteBufferedAsync();

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
    }

    [Test]
    public async Task CannotDoubleSpendArkCash()
    {
        var walletDetails = await FundedWalletHelper.GetFundedWallet();
        var serverInfo = await walletDetails.clientTransport.GetServerInfoAsync();

        var cash = ArkCash.Generate(serverInfo.SignerKey.ToXOnlyPubKey(), serverInfo.UnilateralExit);
        var cashAddress = cash.GetAddress(serverInfo.Network);
        var cashScript = cashAddress.ScriptPubKey.ToHex();

        await Cli.Wrap("docker")
            .WithArguments(["exec", "ark", "ark", "send",
                "--to", cashAddress.ToString(false),
                "--amount", "100000",
                "--password", "secret"])
            .ExecuteBufferedAsync();

        // first wallet claims the ArkCash
        var firstWalletId = await walletDetails.walletProvider.CreateTestWallet();
        await walletDetails.contractService.ImportContract(firstWalletId, cash.ToContract(serverInfo.Network));

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

        var firstBatchTcs = new TaskCompletionSource();
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                firstBatchTcs.TrySetResult();
        };

        var cashVtxoSpentTcs = new TaskCompletionSource();
        walletDetails.vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (vtxo.Script == cashScript && vtxo.IsSpent())
                cashVtxoSpentTcs.TrySetResult();
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

        await firstBatchTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));
        await cashVtxoSpentTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // after claim vtxo at the ArkCash address must be spent
        var spentVtxos = await walletDetails.vtxoStorage.GetVtxos(
            scripts: [cashScript],
            includeSpent: true);

        Assert.That(spentVtxos, Is.Not.Empty, "VTXO at ArkCash address should exist in storage");
        Assert.That(spentVtxos.All(v => v.IsSpent()), Is.True, "All VTXOs at ArkCash address should be spent");

        // no unspent vtox remain so second wallet shouldn't be able to claim anything
        var unspentVtxos = await walletDetails.vtxoStorage.GetVtxos(
            scripts: [cashScript],
            includeSpent: false);

        Assert.That(unspentVtxos, Is.Empty, "No unspent VTXOs should remain at ArkCash address after claim");
    }
}
