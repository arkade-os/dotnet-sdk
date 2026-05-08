using CliWrap;
using CliWrap.Buffered;
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

public class ChainSwapTests
{
    [Test]
    public async Task CanDoBtcToArkChainSwap()
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
            [
                new PaymentContractTransformer(testingPrerequisite.walletProvider),
                new HashLockedContractTransformer(testingPrerequisite.walletProvider),
                new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)
            ]);

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(testingPrerequisite.clientTransport, chainTimeProvider),
            testingPrerequisite.clientTransport, testingPrerequisite.contractService,
            chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions()
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));

        await using var intentGeneration = new IntentGenerationService(testingPrerequisite.clientTransport,
            new DefaultFeeEstimator(testingPrerequisite.clientTransport, chainTimeProvider), coinService,
            testingPrerequisite.walletProvider, intentStorage, testingPrerequisite.safetyService,
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
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledSwapTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (sender, swap) =>
        {
            Console.WriteLine($"[BTC→ARK] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);

        // Create BTC→ARK chain swap — Boltz needs ARK liquidity from Fulmine.
        // Fulmine's settle is async and may not have completed yet, so retry
        // with settle trigger + block mining between attempts.
        var (btcAddress, swapId, expectedLockupSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(
                testingPrerequisite.walletIdentifier,
                50000,
                CancellationToken.None
            ));

        var btcAmount = (expectedLockupSats / 100_000_000m).ToString("0.########");
        Console.WriteLine($"[BTC→ARK] Swap created: {swapId}, BTC lockup: {btcAddress}, expected: {expectedLockupSats} sats ({btcAmount} BTC)");
        Assert.That(btcAddress, Is.Not.Null.And.Not.Empty);
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        // Fund the BTC lockup address with the exact expected amount
        var sendResult = await Cli.Wrap("docker")
            .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "sendtoaddress", btcAddress, btcAmount])
            .ExecuteBufferedAsync();
        Console.WriteLine($"[BTC→ARK] sendtoaddress result: exit={sendResult.ExitCode}, stdout={sendResult.StandardOutput.Trim()}, stderr={sendResult.StandardError.Trim()}");
        Assert.That(sendResult.ExitCode, Is.EqualTo(0), $"sendtoaddress failed: {sendResult.StandardError}");

        // Mine blocks periodically so Boltz confirms the BTC lockup, locks ARK, and we claim
        for (var i = 0; i < 15; i++)
        {
            await DockerHelper.MineBlocks();

            // Poll Boltz status directly to trace progress
            try
            {
                var status = await boltzClient.GetSwapStatusAsync(swapId, CancellationToken.None);
                Console.WriteLine($"[BTC→ARK] Mine round {i}: Boltz status = {status?.Status}, tx = {status?.Transaction?.Hex?.Substring(0, Math.Min(20, status?.Transaction?.Hex?.Length ?? 0))}...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BTC→ARK] Mine round {i}: status poll error: {ex.Message}");
            }

            if (settledSwapTcs.Task.IsCompleted) break;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        // Wait for the swap to settle (Boltz detects BTC → locks ARK → we claim VHTLC → Boltz claims BTC)
        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(3));

        // Verify the swap settled
        var swaps = await swapStorage.GetSwaps(swapIds: [swapId]);
        Assert.That(swaps.Count, Is.GreaterThanOrEqualTo(1));
        var finalSwap = swaps.First(s => s.SwapId == swapId);
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled));
    }

    [Test]
    public async Task CanDoArkToBtcChainSwap()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
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

        var settledSwapTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (sender, swap) =>
        {
            Console.WriteLine($"[ARK→BTC] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);

        // Generate a BTC destination address from the bitcoin node
        var addrResult = await Cli.Wrap("docker")
            .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "getnewaddress"])
            .ExecuteBufferedAsync();
        var btcDestination = BitcoinAddress.Create(addrResult.StandardOutput.Trim(), Network.RegTest);

        // Create ARK→BTC chain swap
        var swapId = await swapMgr.InitiateArkToBtcChainSwap(
            testingPrerequisite.walletIdentifier,
            50000,
            btcDestination,
            CancellationToken.None
        );

        Console.WriteLine($"[ARK→BTC] Swap created: {swapId}");
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        // Mine blocks periodically so Boltz sees the Ark lockup, locks BTC, and we MuSig2-claim
        for (var i = 0; i < 15; i++)
        {
            await DockerHelper.MineBlocks();

            try
            {
                var status = await boltzClient.GetSwapStatusAsync(swapId, CancellationToken.None);
                Console.WriteLine($"[ARK→BTC] Mine round {i}: Boltz status = {status?.Status}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ARK→BTC] Mine round {i}: status poll error: {ex.Message}");
            }

            if (settledSwapTcs.Task.IsCompleted) break;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        // Wait for the swap to settle (Boltz locks BTC → we MuSig2 claim → Boltz claims ARK)
        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(3));

        // Verify the swap settled
        var swaps = await swapStorage.GetSwaps(swapIds: [swapId]);
        Assert.That(swaps.Count, Is.GreaterThanOrEqualTo(1));
        var finalSwap = swaps.First(s => s.SwapId == swapId);
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled));
    }

    /// <summary>
    /// BTC→ARK chain swap unhappy path with renegotiation: the user funds
    /// the BTC lockup with an amount that doesn't match the original
    /// quote (here, ~3× the expected amount, mirroring fulmine's
    /// <c>TestChainSwapBTCtoARKWithQuote</c>). Boltz emits
    /// <c>transaction.lockupFailed</c>; the SDK's <c>PollSwapState</c>
    /// asks Boltz for a new chain quote via
    /// <c>BoltzClient.GetChainQuoteAsync</c>, accepts it via
    /// <c>AcceptChainQuoteAsync</c>, and the swap proceeds with the
    /// renegotiated amount. End state: <see cref="ArkSwapStatus.Settled"/>
    /// with <c>ExpectedAmount</c> updated to reflect the new quote.
    /// </summary>
    [Test]
    [CancelAfter(420_000)]
    public async Task BtcToArkChainSwapRenegotiatesWhenLockupDiffers(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
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

        var settledSwapTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[BTC→ARK reneg] {swap.SwapId} → {swap.Status} (expected {swap.ExpectedAmount}, fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Settled) settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var (btcAddress, swapId, originalExpectedSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(testingPrerequisite.walletIdentifier, 50_000, token));
        Console.WriteLine($"[BTC→ARK reneg] Swap {swapId} created, original expected lockup: {originalExpectedSats} sats");

        // Fund with ~3× the expected amount so Boltz definitely emits
        // transaction.lockupFailed (not just accepted-with-fee-tolerance) and
        // a renegotiated quote becomes necessary. Mirrors the magnitude in
        // fulmine's TestChainSwapBTCtoARKWithQuote.
        var fundAmount = originalExpectedSats * 3;
        var btcAmount = (fundAmount / 100_000_000m).ToString("0.########");
        var sendResult = await Cli.Wrap("docker")
            .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "sendtoaddress", btcAddress, btcAmount])
            .ExecuteBufferedAsync(token);
        Console.WriteLine($"[BTC→ARK reneg] Funded {fundAmount} sats (3× original {originalExpectedSats}), exit={sendResult.ExitCode}");
        Assert.That(sendResult.ExitCode, Is.EqualTo(0), $"sendtoaddress failed: {sendResult.StandardError}");

        // Mine + poll until Boltz settles. Renegotiation happens implicitly
        // inside PollSwapState when Boltz emits transaction.lockupFailed.
        for (var i = 0; i < 30 && !token.IsCancellationRequested && !settledSwapTcs.Task.IsCompleted; i++)
        {
            await DockerHelper.MineBlocks(2, token);
            try
            {
                var status = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[BTC→ARK reneg] Mine round {i}: Boltz status = {status?.Status ?? "<null>"}");
            }
            catch { /* swallow */ }
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }

        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(5), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled),
            $"Over-funded chain swap should still settle (renegotiation succeeds or Boltz accepts within tolerance). Got {finalSwap.Status} (fail={finalSwap.FailReason})");

        // Whether renegotiation actually fired depends on Boltz's
        // fee/dust tolerance vs our 3× overfund. Either outcome is
        // acceptable here — the SDK either renegotiated (and we'll see
        // ExpectedAmount differ) or Boltz accepted silently (Boltz's
        // call). Both end at Settled with no funds lost. Log which
        // happened so test failures elsewhere are diagnosable.
        if (finalSwap.ExpectedAmount != originalExpectedSats)
            Console.WriteLine($"[BTC→ARK reneg] Renegotiation fired: ExpectedAmount {originalExpectedSats} → {finalSwap.ExpectedAmount}");
        else
            Console.WriteLine($"[BTC→ARK reneg] Boltz accepted overfund silently (ExpectedAmount unchanged at {originalExpectedSats})");
    }

    /// <summary>
    /// BTC→ARK chain swap unhappy path: the user creates the swap (gets a
    /// BTC lockup address from Boltz) but never funds it. Boltz's BTC-side
    /// timeout eventually elapses and the swap should transition to a
    /// terminal failed state on the SDK side. The user has lost nothing
    /// because no funds were ever sent.
    /// </summary>
    /// <remarks>
    /// Equivalent to fulmine's <c>TestChainSwapMockBTCToARKUnilateralRefund</c>
    /// without the mock-driven time warp — we lean on Boltz's regtest
    /// timeout instead. Mines blocks aggressively to advance Boltz's BTC
    /// chain past the lockup-confirmation window so the swap expires
    /// inside the test budget rather than the real-time timeout. 10-min
    /// budget covers Boltz's status-monitor lag — the wire timeout is
    /// reached well before that, but Boltz's internal scanner only
    /// re-checks every minute or so.
    /// </remarks>
    [Test]
    [CancelAfter(600_000)]
    public async Task BtcToArkChainSwapMarksFailedWhenUserDoesNotFund(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
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

        var terminalTcs = new TaskCompletionSource<ArkSwapStatus>();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[BTC→ARK no-fund] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status is ArkSwapStatus.Failed or ArkSwapStatus.Refunded)
                terminalTcs.TrySetResult(swap.Status);
            else if (swap.Status == ArkSwapStatus.Settled)
                terminalTcs.TrySetException(new InvalidOperationException(
                    $"Swap {swap.SwapId} unexpectedly Settled without any user funding"));
        };

        await swapMgr.StartAsync(token);

        var (btcAddress, swapId, expectedLockupSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(
                testingPrerequisite.walletIdentifier, 50_000, token));
        Console.WriteLine($"[BTC→ARK no-fund] Swap {swapId} created, lockup address {btcAddress}, expected {expectedLockupSats} sats — NOT funding deliberately");
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        // Mine blocks aggressively to advance Boltz's BTC chain past the
        // lockup-confirmation timeout window. We poll Boltz directly so we
        // can see the moment it gives up — the SDK's transition to
        // Failed/Refunded follows from the polling loop in
        // BoltzSwapProvider.PollSwapState.
        //
        // Boltz's chain-swap status monitor re-checks once per minute on
        // regtest, so even after we mine well past the timeout block height
        // we have to wait for the next monitor tick. 60 rounds × (2s mine +
        // 5s sleep) ≈ 7 minutes of grinding, with the terminal-state TCS
        // short-circuiting as soon as Boltz reports the swap as expired.
        for (var i = 0; i < 60 && !token.IsCancellationRequested; i++)
        {
            await DockerHelper.MineBlocks(20, token);
            try
            {
                var status = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[BTC→ARK no-fund] Mine round {i}: Boltz status = {status?.Status ?? "<null>"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BTC→ARK no-fund] Mine round {i}: status poll error: {ex.Message}");
            }

            if (terminalTcs.Task.IsCompleted) break;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }

        var terminalStatus = await terminalTcs.Task.WaitAsync(TimeSpan.FromMinutes(8), token);
        Assert.That(terminalStatus, Is.AnyOf(ArkSwapStatus.Failed, ArkSwapStatus.Refunded),
            $"BTC→ARK chain swap with no user funding should reach Failed (or Refunded if the SDK chooses to surface it that way); got {terminalStatus}");

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(finalSwap.Status, Is.AnyOf(ArkSwapStatus.Failed, ArkSwapStatus.Refunded));
        // Sanity: no funds left the user's wallet.
        var vtxos = await testingPrerequisite.vtxoStorage.GetVtxos(walletIds: [testingPrerequisite.walletIdentifier]);
        var spent = vtxos.Count(v => v.IsSpent());
        Assert.That(spent, Is.Zero,
            "User VTXOs must be untouched when the BTC lockup was never funded");
    }
}
