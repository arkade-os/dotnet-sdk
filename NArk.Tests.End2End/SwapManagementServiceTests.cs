using CliWrap;
using CliWrap.Buffered;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Blockchain;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
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

        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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

        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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
    /// End-to-end submarine cooperative-refund coverage driven by Boltz's
    /// own <c>boltzr-cli swap set-status invoice.failedToPay</c> admin tool
    /// instead of natural invoice expiry — fires the same nursery event +
    /// websocket update as a real LN payment failure (failure reason
    /// <c>"payment has been cancelled"</c> per
    /// <c>boltz-backend lib/service/Service.ts</c>) so the SDK's
    /// cooperative-refund path runs identically.
    /// <para>
    /// To exercise the multi-VTXO branch we deliberately double-fund the
    /// swap script: the legitimate swap payment lands one VTXO at
    /// <see cref="ArkSwap.ExpectedAmount"/>, then a second VTXO is sent
    /// to the same script via <c>ark send</c> (simulating a wallet
    /// retry / panic-resend). Boltz only signs the cooperative refund
    /// for the canonical lockup VTXO it tracks; extras at the same
    /// script can only be recovered via the timelock path handled by
    /// <c>SweeperService</c> + <c>SwapSweepPolicy</c>. The test asserts
    /// the canonical VTXO is refunded and the extra is left at the
    /// script for the sweeper.
    /// </para>
    /// <para>
    /// The single-VTXO refund variant is implicitly covered: the SDK's
    /// canonical-VTXO selector (<c>vtxos.FirstOrDefault(v =&gt;
    /// v.Amount == swap.ExpectedAmount)</c>) trivially picks the only
    /// VTXO when there's just one.
    /// </para>
    /// </summary>
    [Test]
    [CancelAfter(300_000)]
    public async Task SubmarineRefundsCanonicalVtxoWhenSwapScriptIsDoubleFunded(CancellationToken token)
    {
        var prereq = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }));
        var intentStorage = TestStorage.CreateIntentStorage();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(prereq.clientTransport, prereq.contracts,
        [
            new PaymentContractTransformer(prereq.walletProvider),
            new HashLockedContractTransformer(prereq.walletProvider),
            new VHTLCContractTransformer(prereq.walletProvider, chainTimeProvider)
        ]);
        var spendingService = new SpendingService(prereq.vtxoStorage, prereq.contracts,
            prereq.walletProvider, coinService, prereq.contractService, prereq.clientTransport,
            new DefaultCoinSelector(), prereq.safetyService, intentStorage);
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

        var refundedTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[DoubleFund] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var invoice = await DockerHelper.CreateLndInvoice(amtSats: 50000, expirySecs: 3600, ct: token);
        var swapId = await swapMgr.InitiateSubmarineSwap(prereq.walletIdentifier,
            BOLT11PaymentRequest.Parse(invoice, Network.RegTest), autoPay: false, token);
        Console.WriteLine($"[DoubleFund] Swap {swapId} created");

        try
        {
            // Stop boltz-lnd before paying so Boltz can't race past
            // invoice.failedToPay with a successful 0-conf payment.
            await DockerHelper.StopContainer("boltz-lnd", token);

            // 1st funding: the legitimate swap payment via the SDK.
            await swapMgr.PayExistingSubmarineSwap(prereq.walletIdentifier, swapId, token);

            // 2nd funding: send a small extra VTXO to the same swap script
            // (i.e., simulating a wallet retry / panic-resend by the user).
            // We wait for the canonical lockup to be visible at the script
            // first, then add the extra so both end up there.
            var arkSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
            var canonicalLockupAmount = arkSwap.ExpectedAmount;
            var lockupAddress = arkSwap.Address;
            for (var i = 0; i < 30; i++)
            {
                var found = (await prereq.vtxoStorage.GetVtxos(scripts: [arkSwap.ContractScript], cancellationToken: token))
                    .Any(v => (long)v.Amount == canonicalLockupAmount && !v.IsSpent());
                if (found) break;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            const long extraAmount = 5000;
            Console.WriteLine($"[DoubleFund] Sending extra {extraAmount}-sat VTXO to swap script {lockupAddress}");
            await DockerHelper.SendArkdNoteTo(lockupAddress, extraAmount, token);

            // Wait until BOTH VTXOs are visible at the script in our local
            // VTXO storage (the multi-VTXO state we want to assert on).
            for (var i = 0; i < 30; i++)
            {
                var current = await prereq.vtxoStorage.GetVtxos(scripts: [arkSwap.ContractScript], cancellationToken: token);
                if (current.Count(v => !v.IsSpent()) >= 2) break;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }

            Console.WriteLine($"[DoubleFund] Forcing Boltz invoice.failedToPay via boltzr-cli");
            await DockerHelper.SetBoltzSwapStatus(swapId, "invoice.failedToPay", token);
            // 3 min — the BoltzSwapProvider's near-term retry on missing-VTXO
            // should land the refund within seconds, but allow headroom for
            // the 60s routine-poll fallback to chain twice on slow CI runners.
            await refundedTcs.Task.WaitAsync(TimeSpan.FromMinutes(3), token);

            var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
            Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Refunded),
                $"Cooperative refund should pick the canonical VTXO and complete; got {finalSwap.Status} (fail={finalSwap.FailReason})");

            // The 50k canonical VTXO should now be spent (refunded). The
            // 5k extra remains at the script — the SweeperService takes it
            // via the timelock path eventually, but that's outside the
            // scope of this test.
            var afterRefund = await prereq.vtxoStorage.GetVtxos(scripts: [arkSwap.ContractScript], cancellationToken: token);
            var unspentExtras = afterRefund.Where(v => !v.IsSpent() && (long)v.Amount != canonicalLockupAmount).ToList();
            Assert.That(unspentExtras, Is.Not.Empty,
                "Extra VTXO should still be at the swap script after cooperative refund (sweeper claims it via timelock)");
        }
        finally
        {
            try
            {
                await DockerHelper.StartContainer("boltz-lnd", CancellationToken.None);
                for (var i = 0; i < 30; i++)
                {
                    try
                    {
                        var output = await DockerHelper.Exec("boltz-lnd",
                            ["lncli", "--network=regtest", "getinfo"], CancellationToken.None);
                        if (!string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith('{')) break;
                    }
                    catch { /* not ready yet */ }
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DoubleFund] Warning: boltz-lnd restart wait failed: {ex.Message}");
            }
        }
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
    /// Two fully-independent funded wallets, each with its own
    /// <see cref="SwapsManagementService"/> + <see cref="BoltzSwapProvider"/>
    /// and storage stack, run a submarine swap in parallel. This ensures
    /// the suite handles two simultaneous Boltz websocket subscriptions,
    /// independent status-polling loops, and concurrent settlement
    /// without sharing any local state that could mask races. The
    /// original <c>_swapsIdToWatch HashSet→ConcurrentDictionary</c>
    /// regression is exercised on the websocket-subscription side
    /// because both providers connect to the same Boltz instance.
    /// </remarks>
    [Test]
    [CancelAfter(360_000)]
    public async Task ConcurrentSubmarineSwapsBothComplete(CancellationToken token)
    {
        var prereq1 = await FundedWalletHelper.GetFundedWallet();
        var prereq2 = await FundedWalletHelper.GetFundedWallet();

        var settledA = new TaskCompletionSource();
        var settledB = new TaskCompletionSource();

        async Task<string> RunSwapAsync(
            (ISafetyService safetyService, InMemoryWalletProvider walletProvider, string walletIdentifier,
                IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
                IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) prereq,
            string label, long invoiceSats, TaskCompletionSource settledTcs)
        {
            var swapStorage = TestStorage.CreateSwapStorage();
            var intentStorage = TestStorage.CreateIntentStorage();
            var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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
            var swapMgr = new SwapsManagementService(
                new ISwapProvider[] { boltzProvider },
                spendingService, prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider,
                swapStorage, prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage, chainTimeProvider);

            swapStorage.SwapsChanged += (_, swap) =>
            {
                Console.WriteLine($"[{label}] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
                if (swap.Status == ArkSwapStatus.Settled) settledTcs.TrySetResult();
                else if (swap.Status is ArkSwapStatus.Failed or ArkSwapStatus.Refunded)
                    settledTcs.TrySetException(new InvalidOperationException(
                        $"[{label}] swap {swap.SwapId} hit terminal {swap.Status} before Settled (fail={swap.FailReason})"));
            };

            await swapMgr.StartAsync(token);

            var invoice = BOLT11PaymentRequest.Parse(
                await DockerHelper.CreateLndInvoice(invoiceSats, expirySecs: 0), Network.RegTest);
            var swapId = await swapMgr.InitiateSubmarineSwap(prereq.walletIdentifier, invoice, autoPay: true, token);

            await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(5), token);
            await swapMgr.DisposeAsync();
            return swapId;
        }

        var taskA = RunSwapAsync(prereq1, "ConcurrentA", 8000, settledA);
        var taskB = RunSwapAsync(prereq2, "ConcurrentB", 9000, settledB);
        var swapIds = await Task.WhenAll(taskA, taskB);

        Assert.That(swapIds[0], Is.Not.EqualTo(swapIds[1]),
            "Boltz must hand back distinct swap ids for the two parallel swaps");
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

        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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
    /// <remarks>
    /// Two fully-independent funded wallets, each with its own
    /// <see cref="SwapsManagementService"/> and storage stack — one runs
    /// a submarine swap and the other runs a reverse swap in parallel.
    /// Eliminates intra-wallet coin-selection contention entirely; the
    /// concurrency we exercise is across the two BoltzSwapProvider
    /// instances on shared Boltz infrastructure.
    /// </remarks>
    [Test]
    [CancelAfter(420_000)]
    public async Task SubmarineAndReverseSwapsCompleteInParallel(CancellationToken token)
    {
        var prereqSub = await FundedWalletHelper.GetFundedWallet();
        var prereqRev = await FundedWalletHelper.GetFundedWallet();

        var subSettled = new TaskCompletionSource();
        var revSettled = new TaskCompletionSource();

        async Task RunSubmarineAsync(
            (ISafetyService safetyService, InMemoryWalletProvider walletProvider, string walletIdentifier,
                IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
                IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) prereq)
        {
            var swapStorage = TestStorage.CreateSwapStorage();
            var intentStorage = TestStorage.CreateIntentStorage();
            var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
            var coinService = new CoinService(prereq.clientTransport, prereq.contracts,
            [
                new PaymentContractTransformer(prereq.walletProvider),
                new HashLockedContractTransformer(prereq.walletProvider),
                new VHTLCContractTransformer(prereq.walletProvider, chainTimeProvider)
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
            var swapMgr = new SwapsManagementService(
                new ISwapProvider[] { boltzProvider },
                spendingService, prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider,
                swapStorage, prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage, chainTimeProvider);

            swapStorage.SwapsChanged += (_, swap) =>
            {
                Console.WriteLine($"[ParallelSub] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
                if (swap.Status == ArkSwapStatus.Settled) subSettled.TrySetResult();
                else if (swap.Status is ArkSwapStatus.Failed or ArkSwapStatus.Refunded)
                    subSettled.TrySetException(new InvalidOperationException(
                        $"submarine {swap.SwapId} hit {swap.Status} (fail={swap.FailReason})"));
            };

            await swapMgr.StartAsync(token);
            var invoice = BOLT11PaymentRequest.Parse(
                await DockerHelper.CreateLndInvoice(8000, expirySecs: 0), Network.RegTest);
            await swapMgr.InitiateSubmarineSwap(prereq.walletIdentifier, invoice, autoPay: true, token);
            await subSettled.Task.WaitAsync(TimeSpan.FromMinutes(5), token);
            await swapMgr.DisposeAsync();
        }

        async Task RunReverseAsync(
            (ISafetyService safetyService, InMemoryWalletProvider walletProvider, string walletIdentifier,
                IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
                IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) prereq)
        {
            var swapStorage = TestStorage.CreateSwapStorage();
            var intentStorage = TestStorage.CreateIntentStorage();
            var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
            var coinService = new CoinService(prereq.clientTransport, prereq.contracts,
            [
                new PaymentContractTransformer(prereq.walletProvider),
                new HashLockedContractTransformer(prereq.walletProvider),
                new VHTLCContractTransformer(prereq.walletProvider, chainTimeProvider)
            ]);
            var spendingService = new SpendingService(prereq.vtxoStorage, prereq.contracts,
                prereq.walletProvider, coinService, prereq.contractService, prereq.clientTransport,
                new DefaultCoinSelector(), prereq.safetyService, intentStorage);

            // Mirror the working CanReceiveArkFundsUsingReverseSwap shape:
            // SweeperService + SwapSweepPolicy is what actually claims the
            // user's vHTLC VTXO once Boltz locks ARK on it.
            var sweepMgr = new SweeperService(
                [new SwapSweepPolicy()], prereq.vtxoStorage, coinService, prereq.contracts,
                spendingService, intentStorage,
                new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions
                { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider, []);
            await sweepMgr.StartAsync(token);

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
            var swapMgr = new SwapsManagementService(
                new ISwapProvider[] { boltzProvider },
                spendingService, prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider,
                swapStorage, prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage, chainTimeProvider);

            swapStorage.SwapsChanged += (_, swap) =>
            {
                Console.WriteLine($"[ParallelRev] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
                if (swap.Status == ArkSwapStatus.Settled) revSettled.TrySetResult();
                else if (swap.Status is ArkSwapStatus.Failed or ArkSwapStatus.Refunded)
                    revSettled.TrySetException(new InvalidOperationException(
                        $"reverse {swap.SwapId} hit {swap.Status} (fail={swap.FailReason})"));
            };

            await swapMgr.StartAsync(token);
            var revInvoice = await FulmineLiquidityHelper.RetryWithSettle(() =>
                swapMgr.InitiateReverseSwap(prereq.walletIdentifier,
                    new CreateInvoiceParams(LightMoney.Satoshis(20000), "ParallelReverse", TimeSpan.FromHours(1)),
                    token));
            await Cli.Wrap("docker")
                .WithArguments(["exec", "lnd", "lncli", "--network=regtest", "payinvoice", "--force", revInvoice])
                .ExecuteBufferedAsync(token);
            await revSettled.Task.WaitAsync(TimeSpan.FromMinutes(5), token);
            await swapMgr.DisposeAsync();
            await sweepMgr.DisposeAsync();
        }

        await Task.WhenAll(RunSubmarineAsync(prereqSub), RunReverseAsync(prereqRev));
    }

    [Test]
    public async Task CanRestoreSwapsFromBoltz()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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
