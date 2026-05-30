using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Safety.AsyncKeyedLock;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;
using NArk.Transport.GrpcClient;
using NBitcoin;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End.Core;

/// <summary>
/// Verifies Arkade's correct rejection of sub-dust VTXO operations.
/// </summary>
[TestFixture]
public class SubdustRejectionTests
{
    /// <summary>
    /// A sub-dust VTXO cannot be used as an input in an offchain Arkade transaction.
    /// Alice sends 100 sats (below dust) to Bob. When Bob then tries to forward those
    /// 100 sats, the transaction is rejected because the input VTXO is below the
    /// server's dust threshold.
    /// </summary>
    [Test]
    public async Task SubdustVtxo_SpendOffchain_IsRejected()
    {
        // Alice starts with a normal balance so she can fund Bob with a sub-dust VTXO
        var alice = await FundedWalletHelper.GetFundedWallet(amountSatsPerVtxo: 21_000);

        // Set up Bob's wallet
        var bobStorage = new TestStorage(alice.safetyService);
        var bobVtxoReceivedTcs = new TaskCompletionSource();
        bobStorage.VtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent()) bobVtxoReceivedTcs.TrySetResult();
        };

        var bobWalletProvider = new InMemoryWalletProvider(alice.clientTransport);
        var bobWalletId = await bobWalletProvider.CreateTestWallet();
        var bobContractService = new ContractService(bobWalletProvider, bobStorage.ContractStorage, alice.clientTransport);
        var bobContract = await bobContractService.DeriveContract(bobWalletId, NextContractPurpose.Receive);

        await using var bobVtxoSync = new VtxoSynchronizationService(
            bobStorage.VtxoStorage, alice.clientTransport,
            [bobStorage.VtxoStorage, bobStorage.ContractStorage]);
        await bobVtxoSync.StartAsync(CancellationToken.None);

        // Alice sends 100 sats (sub-dust) to Bob via a direct offchain Arkade tx
        var aliceCoinService = new CoinService(alice.clientTransport, alice.contracts,
            [new PaymentContractTransformer(alice.walletProvider), new HashLockedContractTransformer(alice.walletProvider)]);
        var aliceSpending = new SpendingService(
            alice.vtxoStorage, alice.contracts, alice.walletProvider, aliceCoinService,
            alice.contractService, alice.clientTransport, new DefaultCoinSelector(),
            alice.safetyService, TestStorage.CreateIntentStorage());

        await aliceSpending.Spend(alice.walletIdentifier,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(100), bobContract.GetArkAddress())]);

        await bobVtxoReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Bob now has a 100-sat VTXO. Trying to spend it must be rejected.
        var carolContract = await alice.contractService.DeriveContract(alice.walletIdentifier, NextContractPurpose.Receive);
        var bobCoinService = new CoinService(alice.clientTransport, bobStorage.ContractStorage,
            [new PaymentContractTransformer(bobWalletProvider), new HashLockedContractTransformer(bobWalletProvider)]);
        var bobSpending = new SpendingService(
            bobStorage.VtxoStorage, bobStorage.ContractStorage, bobWalletProvider, bobCoinService,
            bobContractService, alice.clientTransport, new DefaultCoinSelector(),
            alice.safetyService, TestStorage.CreateIntentStorage());

        var ex = Assert.CatchAsync(
            () => bobSpending.Spend(bobWalletId,
                [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(100), carolContract.GetArkAddress())]));
        Assert.That(ex, Is.Not.Null, "Spending a sub-dust VTXO must be rejected");
        Assert.That(ex!.Message, Does.Contain("dust").IgnoreCase,
            "Exception must originate from the dust threshold check, not an unrelated error");
    }

    /// <summary>
    /// The intent scheduler must not attempt to settle a wallet whose total VTXO balance
    /// is below the server's dust threshold — the Arkade server would reject the intent
    /// registration. The scheduler correctly returns an empty intent list in this case.
    /// </summary>
    [Test]
    public async Task SubdustVtxo_IntentScheduler_SkipsSettle()
    {
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var serverInfo = await clientTransport.GetServerInfoAsync();

        Assert.That(100, Is.LessThan((long)serverInfo.Dust.Satoshi),
            $"This test requires 100 sats to be below the server dust ({serverInfo.Dust.Satoshi} sats)");

        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();
        var contractService = new ContractService(walletProvider, storage.ContractStorage, clientTransport);
        var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive);

        await using var vtxoSync = new VtxoSynchronizationService(
            storage.VtxoStorage, clientTransport, [storage.VtxoStorage, storage.ContractStorage]);
        await vtxoSync.StartAsync(CancellationToken.None);

        // Fund Bob with exactly 100 sats — below dust
        await DockerHelper.SendArkdNoteTo(contract.GetArkAddress().ToString(false), 100);

        await TestWaiter.WaitFor(
            async () => (await storage.VtxoStorage.GetVtxos(walletIds: [walletId], includeSpent: false))
                .Any(v => v.Amount == 100),
            timeout: TimeSpan.FromSeconds(15));

        // Build ArkCoins from the sub-dust VTXO
        var coinService = new CoinService(clientTransport, storage.ContractStorage,
            [new PaymentContractTransformer(walletProvider), new HashLockedContractTransformer(walletProvider)]);
        var vtxos = await storage.VtxoStorage.GetVtxos(walletIds: [walletId], includeSpent: false);
        var coins = new List<ArkCoin>();
        foreach (var vtxo in vtxos)
        {
            var entity = (await storage.ContractStorage.GetContracts(walletIds: [walletId]))
                .FirstOrDefault(c => c.Script == vtxo.Script);
            if (entity is null) continue;
            coins.Add(await coinService.GetCoin(entity, vtxo));
        }

        // Scheduler configured with a high threshold — it would normally settle if balance were above dust
        var chainTime = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(clientTransport, chainTime),
            clientTransport, contractService, chainTime,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(
                new SimpleIntentSchedulerOptions { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));

        var intents = await scheduler.GetIntentsToSubmit(coins.AsReadOnly());
        Assert.That(intents, Is.Empty,
            "Scheduler must produce no intents for a wallet with only sub-dust VTXOs");
    }
}
