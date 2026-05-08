using CliWrap;
using CliWrap.Buffered;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NArk.Swaps.Policies;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;
using NBitcoin;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End.Swaps;

public class SwapManagementServiceTests
{
    [Test]

    public async Task CanPayInvoiceWithArkUsingBoltz()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var intentStorage = TestStorage.CreateIntentStorage();

        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
            [new PaymentContractTransformer(testingPrerequisite.walletProvider), new HashLockedContractTransformer(testingPrerequisite.walletProvider)]);
        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                testingPrerequisite.walletProvider,
                coinService,
                testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(), testingPrerequisite.safetyService, intentStorage);
        var boltzProvider = new BoltzSwapProvider(boltzClient, new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions() { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, spendingService, intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);
        await swapMgr.InitiateSubmarineSwap(
            testingPrerequisite.walletIdentifier,
            BOLT11PaymentRequest.Parse(await DockerHelper.CreateLndInvoice(expirySecs: 0), Network.RegTest),
            true,
            CancellationToken.None
        );

        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task CanReceiveArkFundsUsingReverseSwap()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var intentStorage = TestStorage.CreateIntentStorage();

        var options =
            new OptionsWrapper<IntentGenerationServiceOptions>(
                new IntentGenerationServiceOptions() { PollInterval = TimeSpan.FromMinutes(5) }
            );


        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
            [new PaymentContractTransformer(testingPrerequisite.walletProvider), new HashLockedContractTransformer(testingPrerequisite.walletProvider), new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)]);

        // The threshold is so high, it will force an intent generation
        var scheduler = new SimpleIntentScheduler(new DefaultFeeEstimator(testingPrerequisite.clientTransport, chainTimeProvider), testingPrerequisite.clientTransport, testingPrerequisite.contractService,
            chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions()
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));



        await using var intentGeneration = new IntentGenerationService(testingPrerequisite.clientTransport,
            new DefaultFeeEstimator(testingPrerequisite.clientTransport, chainTimeProvider), coinService, testingPrerequisite.walletProvider, intentStorage, testingPrerequisite.safetyService,
            testingPrerequisite.contracts, testingPrerequisite.vtxoStorage, scheduler,
            options);

        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
            testingPrerequisite.walletProvider,
            coinService,
            testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(),
            testingPrerequisite.safetyService, intentStorage);
        await using var sweepMgr = new SweeperService(
            [new SwapSweepPolicy()], testingPrerequisite.vtxoStorage,
            coinService, testingPrerequisite.contracts,
            spendingService, intentStorage,
            new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions()
            { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider, []);
        await sweepMgr.StartAsync(CancellationToken.None);
        var boltzProvider = new BoltzSwapProvider(boltzClient, new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions() { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, spendingService, intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);
        var invoice = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateReverseSwap(
                testingPrerequisite.walletIdentifier,
                new CreateInvoiceParams(LightMoney.Satoshis(50000), "Test", TimeSpan.FromHours(1)),
                CancellationToken.None
            ));

        // Until Aspire has a way to run commands with parameters :(
        await Cli.Wrap("docker")
            .WithArguments(["exec", "lnd", "lncli", "--network=regtest", "payinvoice", "--force", invoice])
            .ExecuteBufferedAsync();

        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task CanDoArkCoOpRefundUsingBoltz()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var intentStorage = TestStorage.CreateIntentStorage();

        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
            [
                new PaymentContractTransformer(testingPrerequisite.walletProvider),
                new HashLockedContractTransformer(testingPrerequisite.walletProvider),
                new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)
            ]);

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var logger = loggerFactory.CreateLogger<SwapsManagementService>();

        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                testingPrerequisite.walletProvider,
                coinService,
                testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(), testingPrerequisite.safetyService, intentStorage);
        var boltzProvider = new BoltzSwapProvider(boltzClient, new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions() { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, spendingService, intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider,
            logger);

        var refundedSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            Console.WriteLine($"[CoOpRefund] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded)
                refundedSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);

        var invoice = await DockerHelper.CreateLndInvoice();
        var swapId = await swapMgr.InitiateSubmarineSwap(
            testingPrerequisite.walletIdentifier,
            BOLT11PaymentRequest.Parse(invoice, Network.RegTest),
            false,
            CancellationToken.None
        );
        Console.WriteLine($"[CoOpRefund] Swap created: {swapId}");

        // wait for invoice to expire
        Console.WriteLine("[CoOpRefund] Waiting 30s for invoice to expire...");
        await Task.Delay(TimeSpan.FromSeconds(30));

        Console.WriteLine("[CoOpRefund] Paying expired swap...");
        await swapMgr.PayExistingSubmarineSwap(testingPrerequisite.walletIdentifier, swapId, CancellationToken.None);
        Console.WriteLine("[CoOpRefund] Payment sent, waiting for cooperative refund...");

        await refundedSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Submarine swap unhappy path: the LN invoice is explicitly cancelled
    /// (via <c>lncli cancelinvoice</c>) before Boltz tries to pay it. Boltz
    /// transitions the swap into <c>invoice.failedToPay</c>, which the SDK
    /// must surface as a cooperative refund — funds returned, swap status
    /// <see cref="ArkSwapStatus.Refunded"/>.
    /// </summary>
    /// <remarks>
    /// More deterministic than the existing <c>CanDoArkCoOpRefundUsingBoltz</c>
    /// which waits 30s for natural invoice expiry; this one drives the
    /// failure state on demand via the LND admin API. Mirrors the
    /// "should automatically refund failed submarine swap" pattern from
    /// <c>arkade-os/boltz-swap</c>'s e2e suite.
    /// </remarks>
    [Test]
    public async Task SubmarineRefundsWhenInvoiceCancelled()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var intentStorage = TestStorage.CreateIntentStorage();

        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
        [
            new PaymentContractTransformer(testingPrerequisite.walletProvider),
            new HashLockedContractTransformer(testingPrerequisite.walletProvider),
            new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)
        ]);

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var logger = loggerFactory.CreateLogger<SwapsManagementService>();

        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
            testingPrerequisite.walletProvider,
            coinService,
            testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(),
            testingPrerequisite.safetyService, intentStorage);
        var boltzProvider = new BoltzSwapProvider(boltzClient, new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions() { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, spendingService, intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider,
            logger);

        var refundedSwapTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[CancelInvoice] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded)
                refundedSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);

        // Long-expiry invoice we then explicitly cancel — the cancel happens
        // before Boltz tries to pay so the swap path is `invoice.failedToPay`
        // rather than expiry-driven.
        var (invoice, rHashHex) = await DockerHelper.CreateLndInvoiceWithHash(expirySecs: 3600);
        await DockerHelper.CancelLndInvoice(rHashHex);
        Console.WriteLine($"[CancelInvoice] Invoice {rHashHex[..12]}… cancelled before swap creation");

        var swapId = await swapMgr.InitiateSubmarineSwap(
            testingPrerequisite.walletIdentifier,
            BOLT11PaymentRequest.Parse(invoice, Network.RegTest),
            autoPay: true,
            CancellationToken.None);
        Console.WriteLine($"[CancelInvoice] Swap {swapId} created against cancelled invoice; waiting for cooperative refund");

        await refundedSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            $"Expected swap {swapId} to be Refunded after invoice cancellation; got {finalSwap.Status} (fail={finalSwap.FailReason})");
    }

    /// <summary>
    /// Concurrent submarine swap stress test: a single wallet + a single
    /// <see cref="BoltzSwapProvider"/> instance host two simultaneous
    /// submarine swaps, both must reach <see cref="ArkSwapStatus.Settled"/>.
    /// Direct regression test for the <c>_swapsIdToWatch</c>
    /// <c>HashSet</c>→<c>ConcurrentDictionary</c> migration — the previous
    /// implementation could silently drop <c>.Remove()</c> calls when the
    /// set was reassigned by <c>DoUpdateStorage</c> while
    /// <c>PollSwapState</c> tried to remove a settled swap from the old
    /// reference.
    /// </summary>
    /// <remarks>
    /// Mirrors fulmine's <c>TestConcurrentSwaps/distinct submarine swaps</c>.
    /// Single wallet so the contention point we want to exercise — provider's
    /// internal state when multiple swap IDs are subscribed and resolved at
    /// staggered times on the same websocket — is actually hit. Per-wallet
    /// coin-selection is serialised by <c>SafetyService</c> locks, so
    /// running two swaps from one wallet is safe.
    /// </remarks>
    [Test]
    public async Task ConcurrentSubmarineSwapsBothComplete()
    {
        var prereq = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(prereq.clientTransport, prereq.contracts,
        [
            new PaymentContractTransformer(prereq.walletProvider),
            new HashLockedContractTransformer(prereq.walletProvider)
        ]);
        var spendingService = new SpendingService(prereq.vtxoStorage, prereq.contracts,
            prereq.walletProvider, coinService, prereq.contractService, prereq.clientTransport,
            new DefaultCoinSelector(), prereq.safetyService, intentStorage);

        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var boltzProvider = new BoltzSwapProvider(boltzClient,
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(),
                new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
                { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider, swapStorage,
            prereq.contractService, prereq.contracts, prereq.safetyService, spendingService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider,
            swapStorage, prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage, chainTimeProvider);

        // Track each swap's settlement independently so we can verify both
        // reached terminal Settled and neither prematurely flipped Failed.
        var settled = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource>();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[Concurrent] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (!settled.TryGetValue(swap.SwapId, out var tcs)) return;
            if (swap.Status == ArkSwapStatus.Settled) tcs.TrySetResult();
            else if (swap.Status is ArkSwapStatus.Failed or ArkSwapStatus.Refunded)
                tcs.TrySetException(new InvalidOperationException(
                    $"swap {swap.SwapId} hit terminal {swap.Status} before Settled (fail={swap.FailReason})"));
        };

        await swapMgr.StartAsync(CancellationToken.None);

        // Fire both InitiateSubmarineSwap calls in parallel so the second
        // swap arrives while the first is still inside the
        // SaveSwap → NotifySwapChanged → trigger-channel → DoUpdateStorage
        // pipeline — that's the window where the two writers race on
        // _swapsIdToWatch in the pre-fix code.
        var inv1 = BOLT11PaymentRequest.Parse(await DockerHelper.CreateLndInvoice(8000, expirySecs: 0), Network.RegTest);
        var inv2 = BOLT11PaymentRequest.Parse(await DockerHelper.CreateLndInvoice(9000, expirySecs: 0), Network.RegTest);

        var swapId1Task = swapMgr.InitiateSubmarineSwap(prereq.walletIdentifier, inv1, autoPay: true);
        var swapId2Task = swapMgr.InitiateSubmarineSwap(prereq.walletIdentifier, inv2, autoPay: true);
        await Task.WhenAll(swapId1Task, swapId2Task);

        var swapId1 = await swapId1Task;
        var swapId2 = await swapId2Task;
        Assert.That(swapId1, Is.Not.EqualTo(swapId2), "Boltz must hand back distinct swap ids");

        settled[swapId1] = new TaskCompletionSource();
        settled[swapId2] = new TaskCompletionSource();

        // Race window: a swap may already have been Settled by the time we
        // attached the TCS above (the SwapsChanged handler short-circuits on
        // a missing key). Reconcile from storage to catch that case.
        foreach (var (swapId, tcs) in settled)
        {
            var current = (await swapStorage.GetSwaps(swapIds: [swapId])).SingleOrDefault();
            if (current?.Status == ArkSwapStatus.Settled) tcs.TrySetResult();
        }

        await Task.WhenAll(
            settled[swapId1].Task.WaitAsync(TimeSpan.FromMinutes(3)),
            settled[swapId2].Task.WaitAsync(TimeSpan.FromMinutes(3)));
    }

    /// <summary>
    /// Boltz submarine pairs publish min/max amount limits; submitting a
    /// swap below the minimum must throw at <c>InitiateSubmarineSwap</c>
    /// time rather than create a doomed swap. Validates the
    /// <c>BoltzLimitsValidator</c> error path the SDK relies on.
    /// </summary>
    [Test]
    public async Task SubmarineSwapBelowMinAmountThrows()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var intentStorage = TestStorage.CreateIntentStorage();

        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
        [
            new PaymentContractTransformer(testingPrerequisite.walletProvider),
            new HashLockedContractTransformer(testingPrerequisite.walletProvider)
        ]);

        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
            testingPrerequisite.walletProvider, coinService, testingPrerequisite.contractService,
            testingPrerequisite.clientTransport, new DefaultCoinSelector(),
            testingPrerequisite.safetyService, intentStorage);

        var boltzProvider = new BoltzSwapProvider(boltzClient,
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(),
                new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
                { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, spendingService, intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        await swapMgr.StartAsync(CancellationToken.None);

        // 1 sat is well below any reasonable submarine swap minimum (Boltz
        // regtest defaults publish 1000 sats min). LND won't even let us
        // create a 1-sat invoice, so use the smallest LND will accept and
        // rely on the Boltz validator to reject.
        var invoice = await DockerHelper.CreateLndInvoice(amtSats: 1, expirySecs: 30);
        var bolt11 = BOLT11PaymentRequest.Parse(invoice, Network.RegTest);

        Assert.That(async () => await swapMgr.InitiateSubmarineSwap(
                testingPrerequisite.walletIdentifier, bolt11, autoPay: true, CancellationToken.None),
            Throws.Exception,
            "Initiating a submarine swap below Boltz's minimum amount should throw at the SDK boundary, not silently create a swap");

        var swaps = await swapStorage.GetSwaps(walletIds: [testingPrerequisite.walletIdentifier]);
        Assert.That(swaps, Is.Empty,
            "Failed limits validation must not persist a swap row");
    }

    /// <summary>
    /// Cross-flow concurrency: a single wallet runs a submarine swap
    /// (LN→Arkade) and a reverse swap (Arkade→LN) simultaneously through
    /// the same <see cref="BoltzSwapProvider"/>. Both must complete. This
    /// stresses the same <c>_swapsIdToWatch</c> ConcurrentDictionary as
    /// <see cref="ConcurrentSubmarineSwapsBothComplete"/> but with two
    /// distinct swap types so coin-selection and contract derivation
    /// take different paths in parallel — analogous to fulmine's
    /// <c>TestConcurrentSwaps/submarine and reverse swaps</c>.
    /// </summary>
    [Test]
    public async Task SubmarineAndReverseSwapsCompleteInParallel()
    {
        var prereq = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(prereq.clientTransport, prereq.contracts,
        [
            new PaymentContractTransformer(prereq.walletProvider),
            new HashLockedContractTransformer(prereq.walletProvider),
            new VHTLCContractTransformer(prereq.walletProvider, chainTimeProvider)
        ]);

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(prereq.clientTransport, chainTimeProvider),
            prereq.clientTransport, prereq.contractService, chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));

        await using var intentGeneration = new IntentGenerationService(prereq.clientTransport,
            new DefaultFeeEstimator(prereq.clientTransport, chainTimeProvider), coinService,
            prereq.walletProvider, intentStorage, prereq.safetyService,
            prereq.contracts, prereq.vtxoStorage, scheduler,
            new OptionsWrapper<IntentGenerationServiceOptions>(new IntentGenerationServiceOptions
            { PollInterval = TimeSpan.FromMinutes(5) }));

        var spendingService = new SpendingService(prereq.vtxoStorage, prereq.contracts,
            prereq.walletProvider, coinService, prereq.contractService, prereq.clientTransport,
            new DefaultCoinSelector(), prereq.safetyService, intentStorage);

        await using var sweepMgr = new SweeperService(
            [new SwapSweepPolicy()], prereq.vtxoStorage, coinService, prereq.contracts,
            spendingService, intentStorage,
            new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions
            { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider, []);
        await sweepMgr.StartAsync(CancellationToken.None);

        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var boltzProvider = new BoltzSwapProvider(boltzClient,
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(),
                new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
                { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider, swapStorage,
            prereq.contractService, prereq.contracts, prereq.safetyService, spendingService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider,
            swapStorage, prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage, chainTimeProvider);

        var settled = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource>();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[Cross] {swap.SwapType} {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (!settled.TryGetValue(swap.SwapId, out var tcs)) return;
            if (swap.Status == ArkSwapStatus.Settled) tcs.TrySetResult();
            else if (swap.Status is ArkSwapStatus.Failed or ArkSwapStatus.Refunded)
                tcs.TrySetException(new InvalidOperationException(
                    $"{swap.SwapType} {swap.SwapId} hit {swap.Status} before Settled (fail={swap.FailReason})"));
        };

        await swapMgr.StartAsync(CancellationToken.None);

        // Fire submarine + reverse in parallel. The submarine pays an LND
        // invoice; the reverse generates an LN invoice that we settle by
        // having LND pay it from the boltz LN node.
        var subInvoice = BOLT11PaymentRequest.Parse(
            await DockerHelper.CreateLndInvoice(8000, expirySecs: 0), Network.RegTest);

        var subTask = swapMgr.InitiateSubmarineSwap(prereq.walletIdentifier, subInvoice, autoPay: true);
        var revInvoiceTask = FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateReverseSwap(prereq.walletIdentifier,
                new CreateInvoiceParams(LightMoney.Satoshis(20000), "ParallelReverse", TimeSpan.FromHours(1)),
                CancellationToken.None));

        await Task.WhenAll(subTask, revInvoiceTask);
        var subSwapId = await subTask;
        var revInvoice = await revInvoiceTask;

        // The reverse swap's record is the most-recently-saved Reverse-type
        // swap belonging to this wallet (the InitiateReverseSwap return is
        // the BOLT11 string, not the swap id).
        var revSwap = (await swapStorage.GetSwaps(walletIds: [prereq.walletIdentifier],
                swapTypes: [ArkSwapType.ReverseSubmarine]))
            .OrderByDescending(s => s.CreatedAt).First();

        settled[subSwapId] = new TaskCompletionSource();
        settled[revSwap.SwapId] = new TaskCompletionSource();

        // Race-window reconciliation (same pattern as ConcurrentSubmarineSwapsBothComplete).
        foreach (var (id, tcs) in settled)
        {
            var current = (await swapStorage.GetSwaps(swapIds: [id])).SingleOrDefault();
            if (current?.Status == ArkSwapStatus.Settled) tcs.TrySetResult();
        }

        // Pay the reverse swap's invoice from LND so Boltz claims its hold.
        await Cli.Wrap("docker")
            .WithArguments(["exec", "lnd", "lncli", "--network=regtest", "payinvoice", "--force", revInvoice])
            .ExecuteBufferedAsync();

        await Task.WhenAll(
            settled[subSwapId].Task.WaitAsync(TimeSpan.FromMinutes(3)),
            settled[revSwap.SwapId].Task.WaitAsync(TimeSpan.FromMinutes(3)));
    }

    [Test]
    public async Task CanRestoreSwapsFromBoltz()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var restoreStorage = new TestStorage(testingPrerequisite.safetyService);
        var swapStorage = restoreStorage.SwapStorage;
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var intentStorage = TestStorage.CreateIntentStorage();

        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
            [
                new PaymentContractTransformer(testingPrerequisite.walletProvider),
                new HashLockedContractTransformer(testingPrerequisite.walletProvider),
                new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)
            ]);

        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                testingPrerequisite.walletProvider,
                coinService,
                testingPrerequisite.contractService, testingPrerequisite.clientTransport, new DefaultCoinSelector(),
                testingPrerequisite.safetyService, intentStorage);
        var boltzProvider = new BoltzSwapProvider(boltzClient, new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions() { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, spendingService, intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        await swapMgr.StartAsync(CancellationToken.None);

        // Create a reverse swap (this creates a swap on Boltz that we can restore later)
        var invoice = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateReverseSwap(
                testingPrerequisite.walletIdentifier,
                new CreateInvoiceParams(LightMoney.Satoshis(50000), "Test Restore", TimeSpan.FromHours(1)),
                CancellationToken.None
            ));
        Assert.That(invoice, Is.Not.Null);

        // Verify the swap was created
        var swapsBeforeClear = await swapStorage.GetSwaps(walletIds: [testingPrerequisite.walletIdentifier]);
        Assert.That(swapsBeforeClear, Has.Count.EqualTo(1));
        var originalSwap = swapsBeforeClear.First();

        // Simulate data loss by clearing the swap storage
        await restoreStorage.ClearSwaps();

        // Verify storage is empty
        var swapsAfterClear = await swapStorage.GetSwaps(walletIds: [testingPrerequisite.walletIdentifier]);
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
        var swapsAfterRestore = await swapStorage.GetSwaps(walletIds: [testingPrerequisite.walletIdentifier]);
        Assert.That(swapsAfterRestore, Has.Count.GreaterThanOrEqualTo(1));
    }
}
