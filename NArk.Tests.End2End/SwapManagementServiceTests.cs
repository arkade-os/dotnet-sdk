using Aspire.Hosting;
using BTCPayServer.Lightning;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NArk.Swaps.Policies;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;
using NBitcoin;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End;

public class SwapManagementServiceTests
{
    private DistributedApplication _app;

    [OneTimeSetUp]
    
    public async Task StartDependencies()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: [],
                configureBuilder: (appOptions, _) => { appOptions.AllowUnsecuredTransport = true; }
            );

        // Start dependencies
        _app = await builder.BuildAsync();
        await _app.StartAsync(CancellationToken.None);
        var waitForBoltzHealthTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("boltz", waitForBoltzHealthTimeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(5)); //Boltz being boltz.... :(
    }

    [OneTimeTearDown]
    public async Task StopDependencies()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Test]

    public async Task CanPayInvoiceWithArkUsingBoltz()
    {
        var boltzApi = _app.GetEndpoint("boltz", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzApi.ToString(), WebsocketUrl = boltzWs.ToString() }));
        var intentStorage = new InMemoryIntentStorage();

        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http"));
        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
            [new PaymentContractTransformer(testingPrerequisite.walletProvider), new HashLockedContractTransformer(testingPrerequisite.walletProvider)]);
        await using var swapMgr = new SwapsManagementService(
            new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                testingPrerequisite.walletProvider,
                coinService,
                testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(), testingPrerequisite.safetyService, intentStorage),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, boltzClient, chainTimeProvider);

        var settledSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);
        await swapMgr.InitiateSubmarineSwap(
            testingPrerequisite.walletIdentifier,
            BOLT11PaymentRequest.Parse((await _app.ResourceCommands.ExecuteCommandAsync("lnd", "create-long-invoice"))
                .ErrorMessage!, Network.RegTest),
            true,
            CancellationToken.None
        );

        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task CanReceiveArkFundsUsingReverseSwap()
    {
        var boltzApi = _app.GetEndpoint("boltz", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http"));
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzApi.ToString(), WebsocketUrl = boltzWs.ToString() }));
        var intentStorage = new InMemoryIntentStorage();

        var options =
            new OptionsWrapper<IntentGenerationServiceOptions>(
                new IntentGenerationServiceOptions() { PollInterval = TimeSpan.FromMinutes(5) }
            );


        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
            [new PaymentContractTransformer(testingPrerequisite.walletProvider), new HashLockedContractTransformer(testingPrerequisite.walletProvider), new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)]);

        // The threshold is so high, it will force an intent generation
        var scheduler = new SimpleIntentScheduler(new DefaultFeeEstimator(testingPrerequisite.clientTransport), testingPrerequisite.clientTransport, testingPrerequisite.contractService,
            new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http")),
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions()
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));



        await using var intentGeneration = new IntentGenerationService(testingPrerequisite.clientTransport,
            new DefaultFeeEstimator(testingPrerequisite.clientTransport), coinService, testingPrerequisite.walletProvider, intentStorage, testingPrerequisite.safetyService,
            testingPrerequisite.contracts, testingPrerequisite.vtxoStorage, scheduler,
            options);

        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
            testingPrerequisite.walletProvider,
            coinService,
            testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(),
            testingPrerequisite.safetyService, intentStorage);
        await using var sweepMgr = new SweeperService(
            new DefaultFeeEstimator(testingPrerequisite.clientTransport),
            [new SwapSweepPolicy()], testingPrerequisite.vtxoStorage,
            intentGeneration, testingPrerequisite.contractService, coinService, testingPrerequisite.contracts,
            spendingService,
            new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions()
            { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider);
        await sweepMgr.StartAsync(CancellationToken.None);
        await using var swapMgr = new SwapsManagementService(
            spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, boltzClient, chainTimeProvider);

        var settledSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);
        var invoice = await swapMgr.InitiateReverseSwap(
            testingPrerequisite.walletIdentifier,
            new CreateInvoiceParams(LightMoney.Satoshis(50000), "Test", TimeSpan.FromHours(1)),
            CancellationToken.None
        );

        // Until Aspire has a way to run commands with parameters :(
        await Cli.Wrap("docker")
            .WithArguments(["exec", "lnd", "lncli", "--network=regtest", "payinvoice", "--force", invoice])
            .ExecuteBufferedAsync();

        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task CanDoArkCoOpRefundUsingBoltz()
    {
        var boltzApi = _app.GetEndpoint("boltz", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzApi.ToString(), WebsocketUrl = boltzWs.ToString() }));
        var intentStorage = new InMemoryIntentStorage();

        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http"));
        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
            [
                new PaymentContractTransformer(testingPrerequisite.walletProvider),
                new HashLockedContractTransformer(testingPrerequisite.walletProvider),
                new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)
            ]);

        await using var swapMgr = new SwapsManagementService(
            new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                testingPrerequisite.walletProvider,
                coinService,
                testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(), testingPrerequisite.safetyService, intentStorage),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, boltzClient, chainTimeProvider);

        var refundedSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Refunded)
                refundedSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);

        var invoice = (await _app.ResourceCommands.ExecuteCommandAsync("lnd", "create-invoice"))
            .ErrorMessage!;
        var swapId = await swapMgr.InitiateSubmarineSwap(
            testingPrerequisite.walletIdentifier,
            BOLT11PaymentRequest.Parse(invoice, Network.RegTest),
            false,
            CancellationToken.None
        );

        // wait for invoice to expire
        await Task.Delay(TimeSpan.FromSeconds(30));

        await swapMgr.PayExistingSubmarineSwap(testingPrerequisite.walletIdentifier, swapId, CancellationToken.None);

        await refundedSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task CanRestoreSwapsFromBoltz()
    {
        var boltzApi = _app.GetEndpoint("boltz", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http"));
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzApi.ToString(), WebsocketUrl = boltzWs.ToString() }));
        var intentStorage = new InMemoryIntentStorage();

        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
            [
                new PaymentContractTransformer(testingPrerequisite.walletProvider),
                new HashLockedContractTransformer(testingPrerequisite.walletProvider),
                new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)
            ]);

        await using var swapMgr = new SwapsManagementService(
            new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                testingPrerequisite.walletProvider,
                coinService,
                testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(),
                testingPrerequisite.safetyService, intentStorage),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, intentStorage, boltzClient, chainTimeProvider);

        await swapMgr.StartAsync(CancellationToken.None);

        // Create a reverse swap (this creates a swap on Boltz that we can restore later)
        var invoice = await swapMgr.InitiateReverseSwap(
            testingPrerequisite.walletIdentifier,
            new CreateInvoiceParams(LightMoney.Satoshis(50000), "Test Restore", TimeSpan.FromHours(1)),
            CancellationToken.None
        );
        Assert.That(invoice, Is.Not.Null);

        // Verify the swap was created
        var swapsBeforeClear = await swapStorage.GetSwaps(testingPrerequisite.walletIdentifier);
        Assert.That(swapsBeforeClear, Has.Count.EqualTo(1));
        var originalSwap = swapsBeforeClear.First();

        // Simulate data loss by clearing the swap storage
        swapStorage.Clear();

        // Verify storage is empty
        var swapsAfterClear = await swapStorage.GetSwaps(testingPrerequisite.walletIdentifier);
        Assert.That(swapsAfterClear, Has.Count.EqualTo(0));

        // Get the descriptors used by the wallet
        var testWallet = testingPrerequisite.walletProvider.GetTestWallet(testingPrerequisite.walletIdentifier);
        Assert.That(testWallet, Is.Not.Null);
        var descriptors = await testWallet!.GetUsedDescriptors();

        // Restore swaps from Boltz
        var restoredSwaps = await swapMgr.RestoreSwaps(
            testingPrerequisite.walletIdentifier,
            descriptors,
            CancellationToken.None
        );

        // Verify the swap was restored
        Assert.That(restoredSwaps, Has.Count.GreaterThanOrEqualTo(1));
        var restoredSwap = restoredSwaps.First(s => s.SwapId == originalSwap.SwapId);
        Assert.That(restoredSwap.SwapType, Is.EqualTo(ArkSwapType.ReverseSubmarine));
        Assert.That(restoredSwap.Address, Is.Not.Empty);

        // Verify the swap is now in storage
        var swapsAfterRestore = await swapStorage.GetSwaps(testingPrerequisite.walletIdentifier);
        Assert.That(swapsAfterRestore, Has.Count.GreaterThanOrEqualTo(1));
    }

}