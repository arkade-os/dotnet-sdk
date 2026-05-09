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
    /// quote — even +1 sat — so Boltz emits
    /// <c>transaction.lockupFailed</c> (chain swaps have zero overpay
    /// tolerance per <c>OverpaymentProtector</c> in boltz-backend). The
    /// SDK's <c>PollSwapState</c> asks Boltz for a new chain quote via
    /// <c>BoltzClient.GetChainQuoteAsync</c>, accepts it via
    /// <c>AcceptChainQuoteAsync</c>, and the swap proceeds with the
    /// renegotiated amount. End state: <see cref="ArkSwapStatus.Settled"/>
    /// with <c>ExpectedAmount</c> reflecting the renegotiated quote.
    /// </summary>
    [Test]
    [CancelAfter(600_000)]
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
            else if (swap.Status is ArkSwapStatus.Failed or ArkSwapStatus.Refunded)
                settledSwapTcs.TrySetException(new InvalidOperationException(
                    $"Renegotiation should settle the swap, not transition to {swap.Status} (fail={swap.FailReason})"));
        };

        await swapMgr.StartAsync(token);

        var (btcAddress, swapId, originalExpectedSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(testingPrerequisite.walletIdentifier, 50_000, token));
        Console.WriteLine($"[BTC→ARK reneg] Swap {swapId} created, original expected lockup: {originalExpectedSats} sats");

        // Boltz chain swaps have zero overpay tolerance — any actual != expected
        // triggers transaction.lockupFailed (boltz-backend OverpaymentProtector).
        // +1000 sats is a clean unambiguous mismatch that's still trivially
        // covered by the test wallet's UTXO budget.
        var fundAmount = originalExpectedSats + 1000;
        var btcAmount = (fundAmount / 100_000_000m).ToString("0.########");
        var sendResult = await Cli.Wrap("docker")
            .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "sendtoaddress", btcAddress, btcAmount])
            .ExecuteBufferedAsync(token);
        Console.WriteLine($"[BTC→ARK reneg] Funded {fundAmount} sats (expected+1000), exit={sendResult.ExitCode}");
        Assert.That(sendResult.ExitCode, Is.EqualTo(0), $"sendtoaddress failed: {sendResult.StandardError}");

        // Mine to confirm the lockup. The SDK's own websocket subscription
        // and PollSwapState drive the state transitions; we just need to
        // give Boltz blocks to confirm the user's tx and tick its monitor.
        for (var i = 0; i < 20 && !token.IsCancellationRequested && !settledSwapTcs.Task.IsCompleted; i++)
        {
            await DockerHelper.MineBlocks(2, token);
            try
            {
                var status = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[BTC→ARK reneg] Mine round {i}: Boltz status = {status?.Status ?? "<null>"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BTC→ARK reneg] Mine round {i}: status poll error: {ex.Message}");
            }
            if (settledSwapTcs.Task.IsCompleted) break;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }

        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(5), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled),
            $"Renegotiated chain swap should settle. Got {finalSwap.Status} (fail={finalSwap.FailReason})");
        Console.WriteLine($"[BTC→ARK reneg] Final ExpectedAmount {originalExpectedSats} → {finalSwap.ExpectedAmount}");
    }

    /// <summary>
    /// BTC→ARK chain swap unhappy path — SDK-side recovery inspection:
    /// the user creates the swap (gets a BTC lockup address) but never
    /// funds it. Boltz on regtest doesn't expire chain swaps that never
    /// see a lockup tx (no on-chain anchor to time the script's CSV
    /// against), but the SDK's <c>ScanRecoverableSwapsAsync</c> can still
    /// classify the abandoned swap as
    /// <see cref="SwapRecoveryStatus.NoFunds"/> by inspecting its own
    /// state — there's no user lockup, no Boltz settled/refunded record,
    /// nothing to recover. That's the path the wallet UI uses to show
    /// "swap stranded, nothing at risk" indicators after a restore.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task BtcToArkChainSwapInspectionReportsNoFundsWhenUnfunded(CancellationToken token)
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

        swapStorage.SwapsChanged += (_, swap) =>
            Console.WriteLine($"[BTC→ARK no-fund] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");

        await swapMgr.StartAsync(token);

        var (btcAddress, swapId, expectedLockupSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(
                testingPrerequisite.walletIdentifier, 50_000, token));
        Console.WriteLine($"[BTC→ARK no-fund] Swap {swapId} created, lockup address {btcAddress}, expected {expectedLockupSats} sats — NOT funding deliberately");
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        // The swap is still Pending from Boltz's perspective forever — Boltz
        // has no on-chain anchor to time out from. Sanity-check that, then
        // verify the SDK's recovery inspection is the right safety net for
        // wallets to surface "abandoned" swaps to the user.
        var inspection = await swapMgr.InspectSwapRecoveryAsync(
            testingPrerequisite.walletIdentifier, swapId, token);
        Console.WriteLine($"[BTC→ARK no-fund] Inspection: status={inspection.Status}, vtxoCount={inspection.VtxoCount}, amountSats={inspection.AmountSats}, error={inspection.Error}");

        Assert.That(inspection.Status, Is.EqualTo(SwapRecoveryStatus.NoFunds),
            $"Unfunded BTC→ARK chain swap should classify as NoFunds (no user lockup, no Boltz settled/refunded); got {inspection.Status} (error={inspection.Error})");
        Assert.That(inspection.VtxoCount, Is.Zero, "No VTXOs should exist at the contract script for an unfunded swap");
        Assert.That(inspection.AmountSats, Is.Zero, "No sats should be observable at the contract script");

        // Sanity: no funds left the user's wallet.
        var vtxos = await testingPrerequisite.vtxoStorage.GetVtxos(walletIds: [testingPrerequisite.walletIdentifier]);
        var spent = vtxos.Count(v => v.IsSpent());
        Assert.That(spent, Is.Zero,
            "User VTXOs must be untouched when the BTC lockup was never funded");

        // ScanRecoverableSwapsAsync mirrors what the wallet UI calls; the
        // unfunded swap should NOT show as Recoverable (nothing to recover).
        var scan = await swapMgr.ScanRecoverableSwapsAsync(testingPrerequisite.walletIdentifier, token);
        Assert.That(scan.Any(s => s.SwapId == swapId && s.Status == SwapRecoveryStatus.Recoverable), Is.False,
            "Unfunded swap must not be flagged as Recoverable in the bulk scan");
    }
}
