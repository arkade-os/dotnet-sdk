using NArk.Tests.End2End.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Contracts;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Storage.EfCore.Hosting;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;
using NBitcoin;

namespace NArk.Tests.End2End.Core;

public class OnchainTests
{
    [Test]
    public async Task CanParticipateInBatchWithColabExit()
    {
        var arkHost =
            Host.CreateDefaultBuilder([])
                .AddArk()
                .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
                .WithSafetyService<AsyncSafetyService>()
                .WithIntentScheduler<SimpleIntentScheduler>()
                .WithWalletProvider<InMemoryWalletProvider>()
                .ConfigureServices((_, s) =>
                {
                    s.AddDbContextFactory<TestDbContext>(options =>
                        options.UseInMemoryDatabase($"Test_{Guid.NewGuid():N}"));
                    s.AddArkEfCoreStorage<TestDbContext>();
                    s.AddNBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
                })
                // Prevent usual intents from getting in the way
                .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
                {
                    o.Threshold = TimeSpan.FromSeconds(2);
                    o.ThresholdHeight = 1;
                }))
                .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(o =>
                    o.PollInterval = TimeSpan.FromSeconds(5)))
                .Build();

        await arkHost.StartAsync();

        var contractService = arkHost.Services.GetRequiredService<IContractService>();
        var wallet = arkHost.Services.GetRequiredService<InMemoryWalletProvider>();
        var intentStorage = arkHost.Services.GetRequiredService<IIntentStorage>();
        var vtxoStorage = arkHost.Services.GetRequiredService<IVtxoStorage>();

        var fp1 = await wallet.CreateTestWallet();
        var fp2 = await wallet.CreateTestWallet();
        var contract = await contractService.DeriveContract(fp1, NextContractPurpose.Receive, cancellationToken: CancellationToken.None);

        var fundedTcs = new TaskCompletionSource();
        vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && vtxo.Amount == 50000UL)
                fundedTcs.TrySetResult();
        };

        await DockerHelper.SendArkdNoteTo(contract.GetArkAddress().ToString(false), 50000);

        await fundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var destination =
            new TaprootAddress(
                new TaprootPubKey((await ((await wallet.GetAddressProviderAsync(fp2))!).GetNextSigningDescriptor()).Extract().XOnlyPubKey!.ToBytes()), Network.RegTest);

        var onchainService = arkHost.Services.GetRequiredService<IOnchainService>();
        await onchainService.InitiateCollaborativeExit(
            fp1,
            new ArkTxOut(
                ArkTxOutType.Onchain,
                10000UL,
                destination
            ),
            CancellationToken.None
        );

        var gotBatchTcs = new TaskCompletionSource();

        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                gotBatchTcs.TrySetResult();
        };

        await gotBatchTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));

        await arkHost.StopAsync();
    }

    /// <summary>
    /// A boarding UTXO (on-chain, not yet settled into the Arkade VTXO tree) cannot be used
    /// as an input to a collaborative exit. arkd rejects the intent and the SDK marks it Cancelled.
    /// </summary>
    [Test]
    public async Task CollabExit_WithBoardingInput_IsRejected()
    {
        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var info = await clientTransport.GetServerInfoAsync();

        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();
        var contractService = new ContractService(walletProvider, storage.ContractStorage, clientTransport);

        // --- 1. Create boarding contract and fund it ---
        var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
            walletId, NextContractPurpose.Boarding, ContractActivityState.Active);

        var onchainAddress = boardingContract.GetOnchainAddress(info.Network).ToString();
        const long boardingAmountSats = 100_000;
        var btcAmount = (boardingAmountSats / 100_000_000m).ToString("0.########",
            System.Globalization.CultureInfo.InvariantCulture);

        await DockerHelper.Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "sendtoaddress", onchainAddress, btcAmount]);
        await DockerHelper.MineBlocks(6);

        // --- 2. Sync boarding UTXO via Esplora ---
        var utxoProvider = new EsploraBlockchain(SharedArkInfrastructure.ChopsticksEndpoint);
        var syncService = new BoardingUtxoSyncService(
            storage.ContractStorage, storage.VtxoStorage, clientTransport, utxoProvider);

        ArkVtxo? boardingVtxo = null;
        for (var i = 0; i < 10; i++)
        {
            await syncService.SyncAsync();
            boardingVtxo = (await storage.VtxoStorage.GetVtxos()).FirstOrDefault(v => !v.IsSpent());
            if (boardingVtxo is not null) break;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Assert.That(boardingVtxo, Is.Not.Null, "Boarding UTXO should sync via Esplora");

        // --- 3. Get boarding coin ---
        var chainTime = new NBXplorerBlockchain(info.Network, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(clientTransport, storage.ContractStorage,
        [
            new PaymentContractTransformer(walletProvider),
            new HashLockedContractTransformer(walletProvider),
            new BoardingContractTransformer(walletProvider),
        ]);
        var boardingCoin = await coinService.GetCoin(boardingVtxo!, walletId);

        // --- 4. Build an onchain destination ---
        var destinationDescriptor = await (await walletProvider.GetAddressProviderAsync(walletId))!
            .GetNextSigningDescriptor();
        var destination = new TaprootAddress(
            new TaprootPubKey(destinationDescriptor.Extract().XOnlyPubKey!.ToBytes()), info.Network);

        var onchainOutput = new ArkTxOut(ArkTxOutType.Onchain, Money.Satoshis(50_000), destination);

        // --- 5. Submit collab exit intent with boarding coin ---
        var intentStorage = TestStorage.CreateIntentStorage();
        var cancelledTcs = new TaskCompletionSource<ArkIntent>();
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.Cancelled)
                cancelledTcs.TrySetResult(intent);
        };

        var intentGenService = new IntentGenerationService(
            clientTransport,
            new DefaultFeeEstimator(clientTransport, chainTime),
            coinService, walletProvider, intentStorage, safetyService,
            storage.ContractStorage, storage.VtxoStorage,
            new SimpleIntentScheduler(new DefaultFeeEstimator(clientTransport, chainTime),
                clientTransport, contractService, chainTime,
                new Microsoft.Extensions.Options.OptionsWrapper<SimpleIntentSchedulerOptions>(
                    new SimpleIntentSchedulerOptions { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 })),
            new Microsoft.Extensions.Options.OptionsWrapper<IntentGenerationServiceOptions>(
                new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromHours(5) }));

        await intentGenService.GenerateManualIntent(walletId,
            new ArkIntentSpec([boardingCoin], [onchainOutput], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));

        await using var intentSync = new IntentSynchronizationService(
            intentStorage, clientTransport, safetyService);
        await intentSync.StartAsync(CancellationToken.None);

        // --- 6. Expect arkd to reject the intent ---
        var cancelled = await cancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That(cancelled.State, Is.EqualTo(ArkIntentState.Cancelled));
        Assert.That(cancelled.CancellationReason, Is.Not.Null.And.Not.Empty,
            "Cancellation reason should contain arkd's rejection message");
    }
}
