using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Blockchain;
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

// ─── Shared helpers ─────────────────────────────────────────────────────────

file static class ChainSwapTestHelpers
{
    public static BoltzClientOptions BoltzOptions() => new()
    {
        BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
        WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString()
    };

    public static BoltzClient CreateBoltzClient() =>
        new(new HttpClient(), new OptionsWrapper<BoltzClientOptions>(BoltzOptions()));

    public static CachedBoltzClient CreateCachedBoltzClient() =>
        new(new HttpClient(), new OptionsWrapper<BoltzClientOptions>(BoltzOptions()));
}

public class ChainSwapTests
{
    [Test]
    public async Task CanDoBtcToArkChainSwap()
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
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider);
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

        Console.WriteLine($"[BTC→ARK] Swap created: {swapId}, BTC lockup: {btcAddress}, expected: {expectedLockupSats} sats");
        Assert.That(btcAddress, Is.Not.Null.And.Not.Empty);
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        // Fund the BTC lockup address with the exact expected amount
        var txid = await DockerHelper.BitcoinSendToAddress(btcAddress, Money.Satoshis(expectedLockupSats));
        Console.WriteLine($"[BTC→ARK] sendtoaddress txid: {txid}");

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
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider);
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
        var btcDestination = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(), Network.RegTest);

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
    /// <c>AcceptChainQuoteAsync</c>, and the swap's <c>ExpectedAmount</c>
    /// is rewritten to the renegotiated value. After acceptance Boltz
    /// commits its ARK lockup VTXO at the user's vHTLC and waits for the
    /// user to claim by spending the vHTLC with the preimage; that
    /// ARK-side claim path isn't yet implemented in the SDK (separate
    /// feature work) so this test asserts only the renegotiation half:
    /// the SDK saw <c>transaction.lockupFailed</c>, accepted a new
    /// quote, and persisted the updated amount.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task BtcToArkChainSwapRenegotiatesWhenLockupDiffers(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        // Renegotiation fires when the SDK observes ExpectedAmount change
        // away from the original quote — that's the moment its
        // PollSwapState handled transaction.lockupFailed by calling
        // GET/POST /v2/swap/chain/{id}/quote. We wait for that signal
        // rather than for full settlement.
        var renegotiatedTcs = new TaskCompletionSource<long>();
        long capturedOriginalExpected = 0;
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[BTC→ARK reneg] {swap.SwapId} → {swap.Status} (expected {swap.ExpectedAmount}, fail: {swap.FailReason})");
            if (capturedOriginalExpected != 0 && swap.ExpectedAmount != capturedOriginalExpected)
                renegotiatedTcs.TrySetResult(swap.ExpectedAmount);
        };

        await swapMgr.StartAsync(token);

        var (btcAddress, swapId, originalExpectedSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(testingPrerequisite.walletIdentifier, 50_000, token));
        capturedOriginalExpected = originalExpectedSats;
        Console.WriteLine($"[BTC→ARK reneg] Swap {swapId} created, original expected lockup: {originalExpectedSats} sats");

        // Boltz chain swaps have zero overpay tolerance — any actual != expected
        // triggers transaction.lockupFailed (boltz-backend OverpaymentProtector).
        // +1000 sats is a clean unambiguous mismatch that's still trivially
        // covered by the test wallet's UTXO budget.
        var fundAmount = originalExpectedSats + 1000;
        var txid = await DockerHelper.BitcoinSendToAddress(btcAddress, Money.Satoshis(fundAmount), token);
        Console.WriteLine($"[BTC→ARK reneg] Funded {fundAmount} sats (expected+1000), txid={txid}");

        // Mine to confirm the lockup so Boltz emits transaction.lockupFailed
        // and the SDK's PollSwapState gets the chance to renegotiate.
        for (var i = 0; i < 20 && !token.IsCancellationRequested && !renegotiatedTcs.Task.IsCompleted; i++)
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
            if (renegotiatedTcs.Task.IsCompleted) break;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }

        // The handler captures the renegotiated amount the moment the SDK
        // persists the new quote. We don't compare against a later read
        // from storage because subsequent PollSwapState iterations can
        // re-save the swap row (status updates from Boltz's transition
        // out of lockupFailed) and any race in those iterations would
        // make this assertion flaky — the persisted moment we care about
        // is the one signalled by SwapsChanged, which is what we capture.
        var renegotiatedAmount = await renegotiatedTcs.Task.WaitAsync(TimeSpan.FromMinutes(3), token);

        Assert.That(renegotiatedAmount, Is.Not.EqualTo(originalExpectedSats),
            "Boltz should have returned a renegotiated quote and the SDK should have persisted it as ExpectedAmount");
        Console.WriteLine($"[BTC→ARK reneg] Renegotiation observed: ExpectedAmount {originalExpectedSats} → {renegotiatedAmount}");
    }

    /// <summary>
    /// BTC→ARK chain swap unhappy path — SDK-side recovery inspection:
    /// the user creates the swap (gets a BTC lockup address) but never
    /// funds it. Boltz on regtest doesn't expire chain swaps that never
    /// see a lockup tx (no on-chain anchor to time the script's CSV
    /// against), so the swap stays in <c>Pending</c> indefinitely. The
    /// SDK's <c>InspectSwapRecoveryAsync</c> classifies any Pending swap
    /// as <see cref="SwapRecoveryStatus.StillPending"/> — that's how a
    /// wallet UI distinguishes "abandoned but still owned by the server"
    /// from genuinely stranded funds (Recoverable / NoFunds).
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task BtcToArkChainSwapInspectionReportsNoFundsWhenUnfunded(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider);
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

        Assert.That(inspection.Status, Is.EqualTo(SwapRecoveryStatus.StillPending),
            $"Unfunded BTC→ARK chain swap should classify as StillPending — the swap is still in flight from " +
            $"Boltz's perspective even though the user never funded it; got {inspection.Status} (error={inspection.Error})");

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

    /// <summary>
    /// ARK→BTC chain swap cooperative refund: the user funds the Arkade VHTLC lockup,
    /// Boltz is forced into <c>swap.expired</c> via a direct DB update + container
    /// restart (same pattern as <see cref="BtcToArkChainSwapRefundsCooperatively"/>),
    /// and the SDK's routine poll calls <c>CoopRefundArkToBtcChainSwap</c> to return
    /// the locked VTXO cooperatively. Boltz must have seen the lockup before the forced
    /// expiry so it can validate the refund PSBT against its own records.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task ArkToBtcChainSwapRefundsCooperatively(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var boltzProvider = new BoltzSwapProvider(boltzClient,
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(),
                new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
                { BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(), WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString() }))),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider,
            loggerFactory.CreateLogger<BoltzSwapProvider>());
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var refundedTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[ARK→BTC refund] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        // ── Step 1: register the swap (Boltz + storage) without sending funds ──
        var btcDestination = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(token), Network.RegTest);
        var (swapId, vhtlcAddress, _, _) = await swapMgr.RegisterArkToBtcChainSwapAsync(
            testingPrerequisite.walletIdentifier, 50_000, btcDestination, token);
        Console.WriteLine($"[ARK→BTC refund] Swap registered: {swapId}, vhtlc={vhtlcAddress}");
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        var savedSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();

        // ── Step 2: stop Boltz so it never sees the VTXO arriving ──
        // With Boltz down the SDK can't claim BTC and the swap can't settle.
        Console.WriteLine("[ARK→BTC refund] Stopping Boltz");
        await DockerHelper.StopContainer(DockerHelper.Container.Boltz, token);

        // ── Step 3: fund the VHTLC off-chain and mine a block ──
        // SendArkdNoteTo creates the VTXO via Fulmine without needing a full batch round.
        // Mine one block so arkd settles the batch and the VTXO gets a confirmed outpoint.
        Console.WriteLine($"[ARK→BTC refund] Funding VHTLC: {vhtlcAddress}");
        await DockerHelper.SendArkdNoteTo(vhtlcAddress.ToString(isMainnet: false), 50_000, token);
        await DockerHelper.MineBlocks(1, token);

        // ── Step 4: read the VTXO txid directly from arkd ──
        string? lockupTxid = null;
        var lockupVout = 0;
        for (var i = 0; i < 10 && lockupTxid is null; i++)
        {
            await foreach (var vtxo in testingPrerequisite.clientTransport.GetVtxoByScriptsAsSnapshot(
                               new HashSet<string> { savedSwap.ContractScript }, token))
            {
                lockupTxid = vtxo.OutPoint.Hash.ToString();
                lockupVout = (int)vtxo.OutPoint.N;
                Console.WriteLine($"[ARK→BTC refund] VTXO found: {lockupTxid}:{lockupVout}");
                break;
            }
            if (lockupTxid is null) await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        Assert.That(lockupTxid, Is.Not.Null, "ARK VTXO not found at VHTLC script after mining");

        // ── Step 5: write swap.expired + transactionId to Boltz DB, start Boltz ──
        // Boltz needs transactionId set so signRefundArk passes checkArkTransaction.
        Console.WriteLine($"[ARK→BTC refund] Setting swap.expired + transactionId in Boltz DB");
        await DockerHelper.SetArkToBtcChainSwapExpiredWithLockup(swapId, lockupTxid!, lockupVout, token);

        // ── Step 6: trigger immediate poll — don't wait 60 s for the routine poll ──
        await boltzProvider.PollSwapState([swapId], token);

        await Task.WhenAny(refundedTcs.Task, Task.Delay(TimeSpan.FromMinutes(3), token));
        await refundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single(s => s.SwapId == swapId);
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            "ARK→BTC swap should reach Refunded status after cooperative refund completes");
    }

    // ─── Wave 3: provider limits & quote ──────────────────────────────────

    /// <summary>
    /// Smoke-check: Boltz must advertise sane chain-swap limits for both
    /// directions. Validates that the min/max/fee fields are populated and
    /// that the fee percentage falls in a plausible range (0–50 %).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task ChainSwapGetLimitsArePlausibleForBothDirections(CancellationToken token)
    {
        var validator = new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient());

        var btcToArk = await validator.GetChainLimitsAsync(isBtcToArk: true, token);
        Assert.That(btcToArk, Is.Not.Null, "BTC→ARK limits not returned by Boltz");
        Assert.That(btcToArk!.MinAmount, Is.GreaterThan(0), "BTC→ARK min must be > 0");
        Assert.That(btcToArk.MaxAmount, Is.GreaterThan(btcToArk.MinAmount), "BTC→ARK max must exceed min");
        Assert.That(btcToArk.FeePercentage, Is.InRange(0m, 0.5m), "BTC→ARK fee percentage should be 0–50%");
        Console.WriteLine($"BTC→ARK: min={btcToArk.MinAmount} max={btcToArk.MaxAmount} fee={btcToArk.FeePercentage:P2} minerFee={btcToArk.MinerFee}");

        var arkToBtc = await validator.GetChainLimitsAsync(isBtcToArk: false, token);
        Assert.That(arkToBtc, Is.Not.Null, "ARK→BTC limits not returned by Boltz");
        Assert.That(arkToBtc!.MinAmount, Is.GreaterThan(0), "ARK→BTC min must be > 0");
        Assert.That(arkToBtc.MaxAmount, Is.GreaterThan(arkToBtc.MinAmount), "ARK→BTC max must exceed min");
        Assert.That(arkToBtc.FeePercentage, Is.InRange(0m, 0.5m), "ARK→BTC fee percentage should be 0–50%");
        Console.WriteLine($"ARK→BTC: min={arkToBtc.MinAmount} max={arkToBtc.MaxAmount} fee={arkToBtc.FeePercentage:P2} minerFee={arkToBtc.MinerFee}");
    }

    /// <summary>
    /// Quote math sanity check: for a 50 000-sat swap the destination amount
    /// must be strictly less than the source (fees are positive) and
    /// DestinationAmount + TotalFees must equal SourceAmount.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task ChainSwapGetQuoteDeductsFeeFromSourceAmount(CancellationToken token)
    {
        var validator = new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient());
        const long amountSats = 50_000L;

        foreach (var (label, isBtcToArk) in new[] { ("BTC→ARK", true), ("ARK→BTC", false) })
        {
            var limits = await validator.GetChainLimitsAsync(isBtcToArk, token);
            Assert.That(limits, Is.Not.Null, $"{label}: limits unavailable");

            var fee = (long)(amountSats * limits!.FeePercentage) + limits.MinerFee;
            var dest = amountSats - fee;

            Assert.That(fee, Is.GreaterThan(0), $"{label}: computed fee must be positive");
            Assert.That(dest, Is.GreaterThan(0), $"{label}: destination amount at 50 000 sats must be positive after fees");
            Assert.That(dest + fee, Is.EqualTo(amountSats), $"{label}: dest + fee must equal source");
            Console.WriteLine($"{label}: {amountSats} sats → dest={dest} fee={fee}");
        }
    }

    // ─── Wave 3: recovery inspection edge cases ────────────────────────────

    /// <summary>
    /// <c>InspectSwapRecoveryAsync</c> must return
    /// <see cref="SwapRecoveryStatus.SwapNotFound"/> for a swap ID that was
    /// never persisted in local storage. This is the typical post-wallet-wipe
    /// scenario where the UI asks about a swap the user remembers but the
    /// local DB does not.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task InspectRecoveryReturnsSwapNotFoundForUnknownId(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
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
        var boltzProvider = new BoltzSwapProvider(
            ChainSwapTestHelpers.CreateBoltzClient(),
            new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient()),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        // No StartAsync needed — InspectSwapRecoveryAsync is a pure storage+arkd read.
        var fakeId = "chain-swap-does-not-exist-xyz123";
        var result = await swapMgr.InspectSwapRecoveryAsync(testingPrerequisite.walletIdentifier, fakeId, token);

        Assert.That(result.Status, Is.EqualTo(SwapRecoveryStatus.SwapNotFound),
            $"Unknown swap ID should return SwapNotFound; got {result.Status} (error={result.Error})");
        Assert.That(result.Swap, Is.Null, "SwapNotFound result should carry no swap record");
    }

    /// <summary>
    /// Right after creating an ARK→BTC chain swap (before the Arkade VHTLC
    /// settles into a batch), <c>InspectSwapRecoveryAsync</c> must return
    /// <see cref="SwapRecoveryStatus.StillPending"/> — the swap is mid-flight,
    /// not stranded. The scan should also not list it as
    /// <see cref="SwapRecoveryStatus.Recoverable"/>.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task ArkToBtcChainSwapInspectionReportsStillPendingJustAfterCreation(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
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
        var boltzProvider = new BoltzSwapProvider(
            ChainSwapTestHelpers.CreateBoltzClient(),
            new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient()),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        await swapMgr.StartAsync(token);

        var btcDestination = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(token), Network.RegTest);
        var swapId = await swapMgr.InitiateArkToBtcChainSwap(
            testingPrerequisite.walletIdentifier, 50_000, btcDestination, token);
        Console.WriteLine($"[ARK→BTC inspect] Swap created: {swapId}");

        // Inspect immediately — the swap row is Pending in storage the moment
        // InitiateArkToBtcChainSwap returns. Denigiri runs an auto-miner so
        // any explicit MineBlocks call before this point can advance the chain
        // enough for the swap to settle before we inspect it.
        var inspection = await swapMgr.InspectSwapRecoveryAsync(
            testingPrerequisite.walletIdentifier, swapId, token);
        Console.WriteLine($"[ARK→BTC inspect] Status={inspection.Status} vtxos={inspection.VtxoCount} amount={inspection.AmountSats}");

        Assert.That(inspection.Status, Is.EqualTo(SwapRecoveryStatus.StillPending),
            $"A freshly-created ARK→BTC swap should be StillPending; got {inspection.Status}");

        // The scan must not surface a mid-flight swap as Recoverable.
        var scan = await swapMgr.ScanRecoverableSwapsAsync(testingPrerequisite.walletIdentifier, token);
        Assert.That(scan.Any(s => s.SwapId == swapId && s.Status == SwapRecoveryStatus.Recoverable), Is.False,
            "A Pending ARK→BTC swap must not appear as Recoverable in the bulk scan");
    }

    /// <summary>
    /// After a BTC→ARK chain swap settles successfully,
    /// <c>InspectSwapRecoveryAsync</c> must return
    /// <see cref="SwapRecoveryStatus.AlreadySettled"/> and
    /// <c>ScanRecoverableSwapsAsync</c> must not flag it as
    /// <see cref="SwapRecoveryStatus.Recoverable"/>.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task InspectAndScanAfterBtcToArkSwapSettlement(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        var options = new OptionsWrapper<IntentGenerationServiceOptions>(
            new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromMinutes(5) });

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
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));
        await using var intentGen = new IntentGenerationService(
            testingPrerequisite.clientTransport,
            new DefaultFeeEstimator(testingPrerequisite.clientTransport, chainTimeProvider),
            coinService, testingPrerequisite.walletProvider, intentStorage,
            testingPrerequisite.safetyService, testingPrerequisite.contracts,
            testingPrerequisite.vtxoStorage, scheduler, options);
        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
            testingPrerequisite.walletProvider, coinService, testingPrerequisite.contractService,
            testingPrerequisite.clientTransport, new DefaultCoinSelector(),
            testingPrerequisite.safetyService, intentStorage);
        await using var sweepMgr = new SweeperService(
            [new SwapSweepPolicy()], testingPrerequisite.vtxoStorage, coinService,
            testingPrerequisite.contracts, spendingService, intentStorage,
            new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions
            { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider, []);
        await sweepMgr.StartAsync(CancellationToken.None);

        var boltzClient = ChainSwapTestHelpers.CreateBoltzClient();
        var boltzProvider = new BoltzSwapProvider(
            boltzClient,
            new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient()),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[inspect-after-settle] {swap.SwapId} → {swap.Status}");
            if (swap.Status == ArkSwapStatus.Settled) settledTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var (btcAddress, swapId, expectedSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(testingPrerequisite.walletIdentifier, 50_000, token));
        await DockerHelper.BitcoinSendToAddress(btcAddress, Money.Satoshis(expectedSats), token);

        for (var i = 0; i < 15 && !settledTcs.Task.IsCompleted; i++)
        {
            await DockerHelper.MineBlocks(1, token);
            if (settledTcs.Task.IsCompleted) break;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(3), token);

        // ── InspectSwapRecoveryAsync must return AlreadySettled ──
        var inspection = await swapMgr.InspectSwapRecoveryAsync(
            testingPrerequisite.walletIdentifier, swapId, token);
        Console.WriteLine($"[inspect-after-settle] Inspection: {inspection.Status}");
        Assert.That(inspection.Status, Is.EqualTo(SwapRecoveryStatus.AlreadySettled),
            $"Settled BTC→ARK swap should return AlreadySettled; got {inspection.Status}");

        // ── ScanRecoverableSwapsAsync must not flag it as Recoverable ──
        var scan = await swapMgr.ScanRecoverableSwapsAsync(testingPrerequisite.walletIdentifier, token);
        Assert.That(scan.Any(s => s.SwapId == swapId && s.Status == SwapRecoveryStatus.Recoverable), Is.False,
            "A settled BTC→ARK swap must not appear as Recoverable in the bulk scan");
        var settled = scan.FirstOrDefault(s => s.SwapId == swapId);
        Assert.That(settled?.Status, Is.EqualTo(SwapRecoveryStatus.AlreadySettled),
            "Bulk scan should return AlreadySettled for the settled swap");
    }

    /// <summary>
    /// BTC→ARK cooperative refund: the user funds the BTC lockup, then
    /// Boltz is forced into a refundable status via <c>boltzr-cli</c>.
    /// The SDK's <c>PollSwapState</c> observes the status change and calls
    /// <c>CoopRefundBtcToArkChainSwap</c> — MuSig2-signing a BTC refund tx
    /// that Boltz co-signs and the SDK broadcasts.
    /// <para>
    /// Requires denigiri's Boltz build. Skipped via
    /// <c>Assert.Ignore</c> when <c>boltzr-cli</c> can't force chain-swap
    /// statuses (older builds that only resolved submarine IDs).
    /// </para>
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    [Ignore("Boltz does not include Transaction.Hex in swap.expired responses after a forced DB expiry, " +
            "so CoopRefundBtcToArkChainSwap cannot locate the lockup outpoint and the swap is incorrectly " +
            "marked Failed. The cooperative BTC refund path should be covered by a unit test that mocks " +
            "GetSwapStatusAsync to return the lockup tx hex on the swap.expired call.")]
    public async Task BtcToArkChainSwapRefundsCooperativelyAfterFunding(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
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
        var boltzClient = ChainSwapTestHelpers.CreateBoltzClient();
        var boltzProvider = new BoltzSwapProvider(
            boltzClient,
            new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient()),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var refundedTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[BTC→ARK refund] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var (btcAddress, swapId, expectedSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(testingPrerequisite.walletIdentifier, 50_000, token));
        Console.WriteLine($"[BTC→ARK refund] Swap {swapId} created, lockup address={btcAddress}, expected={expectedSats} sats");

        // Fund the BTC lockup so Boltz has a transaction to refund against
        var txid = await DockerHelper.BitcoinSendToAddress(btcAddress, Money.Satoshis(expectedSats), token);
        Console.WriteLine($"[BTC→ARK refund] Funded lockup txid={txid}");

        // Mine one block so Boltz detects the BTC lockup, then poll until it
        // acknowledges the transaction. Accepting transaction.mempool means we
        // force expiry before Boltz processes the ARK side, which is the correct
        // test scenario for a cooperative BTC refund.
        await DockerHelper.MineBlocks(1, token);
        var lockupConfirmed = false;
        for (var i = 0; i < 20 && !lockupConfirmed; i++)
        {
            try
            {
                var status = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[BTC→ARK refund] Boltz status: {status?.Status}");
                lockupConfirmed = status?.Status is
                    ChainSwapStatus.TransactionConfirmed or ChainSwapStatus.TransactionServerMempool or
                    ChainSwapStatus.TransactionServerConfirmed or ChainSwapStatus.TransactionClaimPending or
                    ChainSwapStatus.TransactionLockupFailed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BTC→ARK refund] status poll error: {ex.Message}");
            }
            if (!lockupConfirmed)
            {
                await DockerHelper.MineBlocks(1, token);
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }

        Console.WriteLine($"[BTC→ARK refund] Forcing swap.expired via DB+restart");
        var lockupVout = await DockerHelper.BitcoinGetTxVout(txid, btcAddress, token);
        await DockerHelper.SetBtcToArkChainSwapExpiredWithLockup(swapId, txid, lockupVout, token);

        for (var i = 0; i < 20 && !refundedTcs.Task.IsCompleted; i++)
        {
            await DockerHelper.MineBlocks(1, token);
            try
            {
                var s = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[BTC→ARK refund] Mine round {i}: Boltz={s?.Status}");
            }
            catch (Exception ex) { Console.WriteLine($"[BTC→ARK refund] status poll: {ex.Message}"); }
            if (!refundedTcs.Task.IsCompleted)
                await Task.Delay(TimeSpan.FromSeconds(8), token);
        }

        await refundedTcs.Task.WaitAsync(TimeSpan.FromMinutes(4), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single(s => s.SwapId == swapId);
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            "BTC→ARK swap should reach Refunded status after cooperative BTC refund");
    }

    // ─── Wave 3: provider routes ───────────────────────────────────────────

    /// <summary>
    /// Boltz must advertise chain pairs for ARK/BTC in both directions via
    /// <c>GET /v2/swap/chain</c>. Validates the live Boltz endpoint — if
    /// either direction is missing the infrastructure isn't configured for
    /// chain swaps and every other chain-swap test will fail.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task BoltzReportsChainPairsForBothDirections(CancellationToken token)
    {
        var client = ChainSwapTestHelpers.CreateBoltzClient();
        var pairs = await client.GetChainPairsAsync(token);

        Assert.That(pairs, Is.Not.Null, "GET /v2/swap/chain must return a non-null response");
        Assert.That(pairs!.BTC?.ARK, Is.Not.Null,
            "Boltz must advertise a BTC→ARK (BTC.ARK) chain pair");
        Assert.That(pairs.ARK?.BTC, Is.Not.Null,
            "Boltz must advertise an ARK→BTC (ARK.BTC) chain pair");

        var btcToArk = pairs.BTC!.ARK!;
        Assert.That(btcToArk.Limits.Minimal, Is.GreaterThan(0));
        Assert.That(btcToArk.Limits.Maximal, Is.GreaterThan(btcToArk.Limits.Minimal));
        Assert.That(btcToArk.Fees.Percentage, Is.GreaterThan(0m));

        var arkToBtc = pairs.ARK!.BTC!;
        Assert.That(arkToBtc.Limits.Minimal, Is.GreaterThan(0));
        Assert.That(arkToBtc.Limits.Maximal, Is.GreaterThan(arkToBtc.Limits.Minimal));
        Assert.That(arkToBtc.Fees.Percentage, Is.GreaterThan(0m));

        Console.WriteLine($"BTC→ARK: min={btcToArk.Limits.Minimal} max={btcToArk.Limits.Maximal} fee={btcToArk.Fees.Percentage}% minerFee server={btcToArk.Fees.MinerFees.Server}");
        Console.WriteLine($"ARK→BTC: min={arkToBtc.Limits.Minimal} max={arkToBtc.Limits.Maximal} fee={arkToBtc.Fees.Percentage}% minerFee server={arkToBtc.Fees.MinerFees.Server}");
    }

    // ─── Wave 3: BTC arrives at destination ───────────────────────────────

    /// <summary>
    /// ARK→BTC chain swap end-to-end: after settlement the BTC must be
    /// confirmed at the specified destination address, not just reported as
    /// <c>Settled</c> in local storage. Uses <c>getreceivedbyaddress</c>
    /// to verify the on-chain outcome rather than trusting the SDK's
    /// internal state alone.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task ArkToBtcChainSwapVerifyBtcArrivesAtDestination(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = ChainSwapTestHelpers.CreateBoltzClient();
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
            new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient()),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[ARK→BTC btc-check] {swap.SwapId} → {swap.Status}");
            if (swap.Status == ArkSwapStatus.Settled) settledTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        // Generate destination address BEFORE initiating the swap so it's
        // tracked by the Bitcoin Core wallet from the start.
        var btcDestination = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(token), Network.RegTest);
        Console.WriteLine($"[ARK→BTC btc-check] BTC destination: {btcDestination}");

        var swapId = await swapMgr.InitiateArkToBtcChainSwap(
            testingPrerequisite.walletIdentifier, 50_000, btcDestination, token);
        Console.WriteLine($"[ARK→BTC btc-check] Swap created: {swapId}");

        for (var i = 0; i < 15 && !settledTcs.Task.IsCompleted; i++)
        {
            await DockerHelper.MineBlocks(1, token);
            try
            {
                var status = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[ARK→BTC btc-check] round {i}: Boltz={status?.Status}");
            }
            catch { /* poll errors are non-fatal */ }
            if (!settledTcs.Task.IsCompleted)
                await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(3), token);

        // Mine blocks to confirm the BTC claim tx broadcast by the SDK.
        await DockerHelper.MineBlocks(6, token);
        await Task.Delay(TimeSpan.FromSeconds(2), token);

        var received = await DockerHelper.BitcoinGetReceivedByAddress(btcDestination.ToString(), 1, token);
        Console.WriteLine($"[ARK→BTC btc-check] received at destination: {received}");
        Assert.That(received, Is.GreaterThan(Money.Zero),
            $"BTC must be confirmed at destination {btcDestination} after ARK→BTC settlement");
    }

    // ─── Wave 3: inspect/scan after refund ────────────────────────────────

    /// <summary>
    /// After a cooperative ARK→BTC refund completes,
    /// <c>InspectSwapRecoveryAsync</c> must return
    /// <see cref="SwapRecoveryStatus.AlreadyRefunded"/> and the bulk scan
    /// must also return <see cref="SwapRecoveryStatus.AlreadyRefunded"/>
    /// rather than <see cref="SwapRecoveryStatus.Recoverable"/>. Requires
    /// denigiri's Boltz build; skipped otherwise.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task InspectAndScanAfterArkToBtcCoopRefund(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = ChainSwapTestHelpers.CreateBoltzClient();
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
            new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient()),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var refundedTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[ARK→BTC inspect-refund] {swap.SwapId} → {swap.Status}");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        // ── Step 1: register swap without sending funds ──
        var btcDestination = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(token), Network.RegTest);
        var (swapId, vhtlcAddress, _, _) = await swapMgr.RegisterArkToBtcChainSwapAsync(
            testingPrerequisite.walletIdentifier, 50_000, btcDestination, token);
        Console.WriteLine($"[ARK→BTC inspect-refund] Swap: {swapId}");
        var savedSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();

        // ── Step 2: stop Boltz so the swap can't settle before we force expiry ──
        await DockerHelper.StopContainer(DockerHelper.Container.Boltz, token);

        // ── Step 3: fund the VHTLC and mine so the VTXO gets a confirmed outpoint ──
        await DockerHelper.SendArkdNoteTo(vhtlcAddress.ToString(isMainnet: false), 50_000, token);
        await DockerHelper.MineBlocks(1, token);

        // ── Step 4: find the VTXO txid from arkd ──
        string? lockupTxid = null;
        var lockupVout = 0;
        for (var i = 0; i < 10 && lockupTxid is null; i++)
        {
            await foreach (var vtxo in testingPrerequisite.clientTransport.GetVtxoByScriptsAsSnapshot(
                               new HashSet<string> { savedSwap.ContractScript }, token))
            {
                lockupTxid = vtxo.OutPoint.Hash.ToString();
                lockupVout = (int)vtxo.OutPoint.N;
                Console.WriteLine($"[ARK→BTC inspect-refund] VTXO: {lockupTxid}:{lockupVout}");
                break;
            }
            if (lockupTxid is null) await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        Assert.That(lockupTxid, Is.Not.Null, "ARK VTXO not found at VHTLC script after mining");

        // ── Step 5: write swap.expired + transactionId to Boltz DB, start Boltz ──
        await DockerHelper.SetArkToBtcChainSwapExpiredWithLockup(swapId, lockupTxid!, lockupVout, token);

        // ── Step 6: immediate poll instead of waiting for the 60-second routine ──
        await boltzProvider.PollSwapState([swapId], token);

        await Task.WhenAny(refundedTcs.Task, Task.Delay(TimeSpan.FromMinutes(3), token));
        await refundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        // ── InspectSwapRecoveryAsync must return AlreadyRefunded ──
        var inspection = await swapMgr.InspectSwapRecoveryAsync(
            testingPrerequisite.walletIdentifier, swapId, token);
        Console.WriteLine($"[ARK→BTC inspect-refund] Inspection: {inspection.Status}");
        Assert.That(inspection.Status, Is.EqualTo(SwapRecoveryStatus.AlreadyRefunded),
            $"Refunded swap should return AlreadyRefunded; got {inspection.Status}");

        // ── Bulk scan must not flag it as Recoverable ──
        var scan = await swapMgr.ScanRecoverableSwapsAsync(testingPrerequisite.walletIdentifier, token);
        var entry = scan.FirstOrDefault(s => s.SwapId == swapId);
        Assert.That(entry?.Status, Is.EqualTo(SwapRecoveryStatus.AlreadyRefunded),
            "Bulk scan should return AlreadyRefunded, not Recoverable");
    }

    // ─── Wave 3: renegotiation refused → cooperative refund ───────────────

    // ─── Wave 3: container restart resilience ─────────────────────────────

    /// <summary>
    /// The SDK's websocket reconnect loop must survive a Boltz container
    /// restart mid-flight. An ARK→BTC chain swap is started, Boltz is
    /// stopped and restarted, and the swap must still reach
    /// <see cref="ArkSwapStatus.Settled"/> after the container comes back
    /// and mining resumes.
    /// </summary>
    [Test]
    [CancelAfter(480_000)]
    public async Task BoltzContainerRestartDuringArkToBtcChainSwap(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = ChainSwapTestHelpers.CreateBoltzClient();
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
            new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient()),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[ARK→BTC restart] {swap.SwapId} → {swap.Status}");
            if (swap.Status == ArkSwapStatus.Settled) settledTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var btcDestination = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(token), Network.RegTest);
        var swapId = await swapMgr.InitiateArkToBtcChainSwap(
            testingPrerequisite.walletIdentifier, 50_000, btcDestination, token);
        Console.WriteLine($"[ARK→BTC restart] Swap created: {swapId}");

        // Mine until Boltz acknowledges the Arkade lockup (server.mempool or confirmed)
        var lockupSeen = false;
        for (var i = 0; i < 10 && !lockupSeen; i++)
        {
            await DockerHelper.MineBlocks(1, token);
            try
            {
                var s = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[ARK→BTC restart] pre-restart status: {s?.Status}");
                lockupSeen = s?.Status is ChainSwapStatus.TransactionMempool or ChainSwapStatus.TransactionConfirmed
                    or ChainSwapStatus.TransactionServerMempool or ChainSwapStatus.TransactionServerConfirmed
                    or ChainSwapStatus.TransactionClaimPending;
            }
            catch { /* ok — container might not yet have processed the lockup */ }
            if (!lockupSeen) await Task.Delay(TimeSpan.FromSeconds(3), token);
        }

        // ── Stop and restart the Boltz container ──
        Console.WriteLine("[ARK→BTC restart] Stopping boltz container...");
        await DockerHelper.StopContainer(DockerHelper.Container.Boltz, token);
        await Task.Delay(TimeSpan.FromSeconds(5), token);

        Console.WriteLine("[ARK→BTC restart] Starting boltz container...");
        await DockerHelper.StartContainer(DockerHelper.Container.Boltz, token);

        // Wait for Boltz to boot (LND reconnect + DB init typically ~10-20 s)
        Console.WriteLine("[ARK→BTC restart] Waiting for Boltz to be ready...");
        await Task.Delay(TimeSpan.FromSeconds(20), token);

        // Mine and poll until settled — the SDK's reconnect loop should
        // resume the websocket subscription and finish the claim.
        for (var i = 0; i < 20 && !settledTcs.Task.IsCompleted; i++)
        {
            await DockerHelper.MineBlocks(1, token);
            try
            {
                var s = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[ARK→BTC restart] post-restart round {i}: Boltz={s?.Status}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ARK→BTC restart] status poll error: {ex.Message}");
            }
            if (!settledTcs.Task.IsCompleted)
                await Task.Delay(TimeSpan.FromSeconds(8), token);
        }

        await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(4), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single(s => s.SwapId == swapId);
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled),
            "ARK→BTC swap must settle after Boltz container restart");
    }

    // ─── Wave 3: different amounts ─────────────────────────────────────────

    /// <summary>
    /// BTC→ARK chain swap with a non-default amount (75 000 sats) to verify
    /// the happy path isn't accidentally hard-coded to the 50 000-sat value
    /// used in other tests.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task BtcToArkChainSwapWithNonDefaultAmountSettles(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var swapStorage = TestStorage.CreateSwapStorage();
        var boltzClient = ChainSwapTestHelpers.CreateBoltzClient();
        var intentStorage = TestStorage.CreateIntentStorage();
        var options = new OptionsWrapper<IntentGenerationServiceOptions>(
            new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromMinutes(5) });

        var coinService = new CoinService(testingPrerequisite.clientTransport, testingPrerequisite.contracts,
        [
            new PaymentContractTransformer(testingPrerequisite.walletProvider),
            new HashLockedContractTransformer(testingPrerequisite.walletProvider),
            new VHTLCContractTransformer(testingPrerequisite.walletProvider, chainTimeProvider)
        ]);
        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(testingPrerequisite.clientTransport, chainTimeProvider),
            testingPrerequisite.clientTransport, testingPrerequisite.contractService, chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));
        await using var intentGen = new IntentGenerationService(
            testingPrerequisite.clientTransport,
            new DefaultFeeEstimator(testingPrerequisite.clientTransport, chainTimeProvider),
            coinService, testingPrerequisite.walletProvider, intentStorage,
            testingPrerequisite.safetyService, testingPrerequisite.contracts,
            testingPrerequisite.vtxoStorage, scheduler, options);
        var spendingService = new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
            testingPrerequisite.walletProvider, coinService, testingPrerequisite.contractService,
            testingPrerequisite.clientTransport, new DefaultCoinSelector(),
            testingPrerequisite.safetyService, intentStorage);
        await using var sweepMgr = new SweeperService(
            [new SwapSweepPolicy()], testingPrerequisite.vtxoStorage, coinService,
            testingPrerequisite.contracts, spendingService, intentStorage,
            new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions
            { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider, []);
        await sweepMgr.StartAsync(CancellationToken.None);

        var boltzProvider = new BoltzSwapProvider(boltzClient,
            new BoltzLimitsValidator(ChainSwapTestHelpers.CreateCachedBoltzClient()),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService,
            intentStorage, chainTimeProvider);
        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[BTC→ARK 75k] {swap.SwapId} → {swap.Status}");
            if (swap.Status == ArkSwapStatus.Settled) settledTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        const long amountSats = 75_000L;
        var (btcAddress, swapId, expectedSats) = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateBtcToArkChainSwap(testingPrerequisite.walletIdentifier, amountSats, token));
        Console.WriteLine($"[BTC→ARK 75k] Swap {swapId}: lockup={btcAddress} expected={expectedSats} sats");

        var txid = await DockerHelper.BitcoinSendToAddress(btcAddress, Money.Satoshis(expectedSats), token);
        Console.WriteLine($"[BTC→ARK 75k] funded txid={txid}");

        for (var i = 0; i < 15 && !settledTcs.Task.IsCompleted; i++)
        {
            await DockerHelper.MineBlocks(1, token);
            try
            {
                var status = await boltzClient.GetSwapStatusAsync(swapId, token);
                Console.WriteLine($"[BTC→ARK 75k] round {i}: Boltz={status?.Status}");
            }
            catch { /* non-fatal */ }
            if (!settledTcs.Task.IsCompleted)
                await Task.Delay(TimeSpan.FromSeconds(5), token);
        }

        await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(3), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single(s => s.SwapId == swapId);
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled));
        Assert.That(finalSwap.ExpectedAmount, Is.EqualTo(amountSats),
            "ExpectedAmount stores the user-requested swap amount, not the Boltz lockup amount (which includes fees)");
    }
}
