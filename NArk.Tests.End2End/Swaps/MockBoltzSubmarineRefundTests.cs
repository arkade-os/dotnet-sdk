using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.Mocks;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Swaps;

/// <summary>
/// Submarine swap refund scenarios exercised with the in-process
/// <see cref="MockBoltzServer"/> so each test is isolated, fast, and
/// deterministic — no real Boltz container restart or 30-second invoice
/// expiry wait required.
///
/// The real Arkade network (arkd + VtxoSynchronizationService) is still
/// used for the ARK-side locking step so the SDK's full VTXO-spending
/// path is exercised end-to-end.
/// </summary>
[NonParallelizable]
public class MockBoltzSubmarineRefundTests
{
    private static SwapsManagementService BuildSwapMgr(
        MockBoltzServer mock,
        ISafetyService safetyService,
        IWalletProvider walletProvider,
        IVtxoStorage vtxoStorage,
        ContractService contractService,
        IContractStorage contracts,
        NArk.Core.Transport.IClientTransport clientTransport,
        ISwapStorage swapStorage,
        IIntentStorage intentStorage,
        IIntentGenerationService? intentGenerationService = null)
    {
        var opts = new BoltzClientOptions
            { BoltzUrl = mock.BaseUrl, WebsocketUrl = mock.WsBaseUrl };
        var optsWrapper = new OptionsWrapper<BoltzClientOptions>(opts);
        var boltzClient = new BoltzClient(new HttpClient(), optsWrapper);
        var chainTimeProvider = new NBXplorerBlockchain(
            Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);

        var coinService = new CoinService(clientTransport, contracts,
        [
            new PaymentContractTransformer(walletProvider),
            new HashLockedContractTransformer(walletProvider),
            new VHTLCContractTransformer(walletProvider, chainTimeProvider)
        ]);
        var spendingService = new SpendingService(
            vtxoStorage, contracts, walletProvider,
            coinService, contractService, clientTransport,
            new DefaultCoinSelector(), safetyService, intentStorage);
        var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Debug));
        var boltzProvider = new BoltzSwapProvider(
            boltzClient,
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), optsWrapper)),
            clientTransport, vtxoStorage, walletProvider,
            swapStorage, contractService, contracts,
            safetyService, intentStorage, chainTimeProvider,
            intentGenerationService,
            loggerFactory.CreateLogger<BoltzSwapProvider>());

        return new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, clientTransport, vtxoStorage,
            walletProvider, swapStorage, contractService,
            contracts, safetyService, intentStorage, chainTimeProvider);
    }

    private static Task WaitForVtxoAtScript(
        IVtxoStorage vtxoStorage,
        string contractScript,
        long expectedAmount,
        CancellationToken ct) =>
        TestWaiter.WaitFor(
            async () =>
            {
                var vtxos = await vtxoStorage.GetVtxos(scripts: [contractScript], cancellationToken: ct);
                return vtxos.Any(v => (long)v.Amount == expectedAmount && !v.IsSpent());
            },
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromSeconds(1),
            ct: ct);

    // ── Test 1: invoice.failedToPay ─────────────────────────────────────

    /// <summary>
    /// Mock Boltz pushes <c>invoice.failedToPay</c> after the ARK lockup
    /// VTXO arrives. The SDK must cooperatively refund via
    /// <c>POST /v2/swap/submarine/{id}/refund/ark</c> (signed by the mock)
    /// and transition the swap to <see cref="ArkSwapStatus.Refunded"/>.
    ///
    /// This is the single-VTXO counterpart of
    /// <see cref="SwapManagementServiceTests.SubmarineRefundsCanonicalVtxoWhenSwapScriptIsDoubleFunded"/>
    /// which exercises the same cooperative-refund path but via real
    /// <c>boltzr-cli</c> and a double-funded swap script.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task SubmarineRefund_InvoiceFailedToPay(CancellationToken token)
    {
        await using var mock = await MockBoltzServer.StartAsync();
        var prereq = await FundedWalletHelper.GetFundedWallet();
        mock.ServerInfo = await prereq.clientTransport.GetServerInfoAsync();

        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        await using var swapMgr = BuildSwapMgr(mock,
            prereq.safetyService, prereq.walletProvider, prereq.vtxoStorage,
            prereq.contractService, prereq.contracts, prereq.clientTransport,
            swapStorage, intentStorage);

        var refundedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[InvoiceFailed] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        // Use a long-lived invoice so it doesn't expire before we assert.
        var invoice = await DockerHelper.CreateLndInvoice(amtSats: 50_000, expirySecs: 3600, ct: token);
        var swapId = await swapMgr.InitiateSubmarineSwap(
            prereq.walletIdentifier,
            BOLT11PaymentRequest.Parse(invoice, Network.RegTest),
            autoPay: false,
            token);
        Console.WriteLine($"[InvoiceFailed] Swap {swapId} created");

        var arkSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        await swapMgr.PayExistingSubmarineSwap(prereq.walletIdentifier, swapId, token);

        Console.WriteLine("[InvoiceFailed] Waiting for VTXO at swap script...");
        await WaitForVtxoAtScript(prereq.vtxoStorage, arkSwap.ContractScript, arkSwap.ExpectedAmount, token);

        Console.WriteLine("[InvoiceFailed] Pushing invoice.failedToPay");
        await mock.PushSwapEvent(swapId, "invoice.failedToPay", token);

        await refundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);

        var final = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(final.Status, Is.EqualTo(ArkSwapStatus.Refunded));
        Assert.That(mock.SubmarineRefundRequestsFor(swapId), Is.GreaterThan(0),
            "SDK must have called POST /v2/swap/submarine/{id}/refund/ark");
    }

    // ── Test 2: transaction.lockupFailed ───────────────────────────────

    /// <summary>
    /// Mock Boltz pushes <c>transaction.lockupFailed</c> (e.g., amount
    /// mismatch detected on-chain). The SDK must still trigger the
    /// cooperative-refund path — <see cref="BoltzOperationClassifier"/>
    /// treats <c>transaction.lockupFailed</c> as a refundable status for
    /// submarine swaps.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task SubmarineRefund_TransactionLockupFailed(CancellationToken token)
    {
        await using var mock = await MockBoltzServer.StartAsync();
        var prereq = await FundedWalletHelper.GetFundedWallet();
        mock.ServerInfo = await prereq.clientTransport.GetServerInfoAsync();

        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        await using var swapMgr = BuildSwapMgr(mock,
            prereq.safetyService, prereq.walletProvider, prereq.vtxoStorage,
            prereq.contractService, prereq.contracts, prereq.clientTransport,
            swapStorage, intentStorage);

        var refundedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[LockupFailed] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var invoice = await DockerHelper.CreateLndInvoice(amtSats: 50_000, expirySecs: 3600, ct: token);
        var swapId = await swapMgr.InitiateSubmarineSwap(
            prereq.walletIdentifier,
            BOLT11PaymentRequest.Parse(invoice, Network.RegTest),
            autoPay: false,
            token);
        Console.WriteLine($"[LockupFailed] Swap {swapId} created");

        var arkSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        await swapMgr.PayExistingSubmarineSwap(prereq.walletIdentifier, swapId, token);

        Console.WriteLine("[LockupFailed] Waiting for VTXO at swap script...");
        await WaitForVtxoAtScript(prereq.vtxoStorage, arkSwap.ContractScript, arkSwap.ExpectedAmount, token);

        Console.WriteLine("[LockupFailed] Pushing transaction.lockupFailed");
        await mock.PushSwapEvent(swapId, "transaction.lockupFailed", token);

        await refundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);

        var final = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(final.Status, Is.EqualTo(ArkSwapStatus.Refunded));
        Assert.That(mock.SubmarineRefundRequestsFor(swapId), Is.GreaterThan(0),
            "SDK must have called POST /v2/swap/submarine/{id}/refund/ark");
    }

    // ── Test 3: Boltz refuses co-sign ──────────────────────────────────

    /// <summary>
    /// Boltz refuses every cooperative-refund co-sign request
    /// (<c>RefundMode.Fail</c>). After at least one refused attempt the SDK must
    /// fall back to the <c>refundWithoutReceiver</c> Arkade batch exit — the VHTLC
    /// is spent via the sender + Arkade-server tapscript (absolute CLTV, already
    /// elapsed) as a batch intent, returning the funds to the user's wallet inside
    /// Arkade without Boltz. The swap must reach <see cref="ArkSwapStatus.Refunded"/>.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task SubmarineRefund_BoltzRefusesCosign_FallsBackToJoinBatch(CancellationToken token)
    {
        await using var mock = await MockBoltzServer.StartAsync();
        mock.SetRefundMode(RefundMode.Fail);

        var prereq = await FundedWalletHelper.GetFundedWallet();
        mock.ServerInfo = await prereq.clientTransport.GetServerInfoAsync();

        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();

        // Full batch stack so the refundWithoutReceiver spend can settle as a manual
        // batch intent (the checkpoint/SubmitTx path can't be used here — the closure's
        // sender+server multisig needs the Arkade server's forfeit co-sign via JoinRound).
        var blockchain = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(prereq.clientTransport, prereq.contracts,
        [
            new PaymentContractTransformer(prereq.walletProvider),
            new HashLockedContractTransformer(prereq.walletProvider),
            new VHTLCContractTransformer(prereq.walletProvider, blockchain)
        ]);

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(prereq.clientTransport, blockchain),
            prereq.clientTransport, prereq.contractService, blockchain,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(
                new SimpleIntentSchedulerOptions { Threshold = TimeSpan.FromHours(24), ThresholdHeight = 100_000 }));

        // NOTE: we deliberately do NOT call intentGeneration.StartAsync(). Its first
        // poll cycle would auto-register the wallet's VTXOs for a batch, racing the
        // submarine funding spend below (VTXO_ALREADY_REGISTERED). The refund uses
        // GenerateManualIntent (a direct call), which doesn't need the poll loop.
        await using var intentGeneration = new IntentGenerationService(
            prereq.clientTransport,
            new DefaultFeeEstimator(prereq.clientTransport, blockchain),
            coinService, prereq.walletProvider, intentStorage,
            prereq.safetyService, prereq.contracts, prereq.vtxoStorage, scheduler,
            new OptionsWrapper<IntentGenerationServiceOptions>(new IntentGenerationServiceOptions
                { PollInterval = TimeSpan.FromHours(5) }));

        await using var intentSync = new IntentSynchronizationService(
            intentStorage, prereq.clientTransport, prereq.safetyService);
        await intentSync.StartAsync(token);

        await using var batchManager = new BatchManagementService(
            intentStorage, prereq.clientTransport, prereq.vtxoStorage,
            prereq.contracts, prereq.walletProvider, coinService, prereq.safetyService);
        await batchManager.StartAsync(token);

        await using var swapMgr = BuildSwapMgr(mock,
            prereq.safetyService, prereq.walletProvider, prereq.vtxoStorage,
            prereq.contractService, prereq.contracts, prereq.clientTransport,
            swapStorage, intentStorage, intentGeneration);

        var refundedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[RefusesCosign] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var invoice = await DockerHelper.CreateLndInvoice(amtSats: 50_000, expirySecs: 3600, ct: token);
        var swapId = await swapMgr.InitiateSubmarineSwap(
            prereq.walletIdentifier,
            BOLT11PaymentRequest.Parse(invoice, Network.RegTest),
            autoPay: false,
            token);

        var arkSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        await swapMgr.PayExistingSubmarineSwap(prereq.walletIdentifier, swapId, token);

        await WaitForVtxoAtScript(prereq.vtxoStorage, arkSwap.ContractScript, arkSwap.ExpectedAmount, token);

        // Nudge the next Arkade batch session; refundWithoutReceiver locktime is a past
        // timestamp (mock DefaultTimeouts) so it is already elapsed.
        await DockerHelper.MineBlocks(1, token);
        await mock.PushSwapEvent(swapId, "invoice.failedToPay", token);

        await refundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(120), token);

        var final = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(final.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            "Swap must reach Refunded via the refundWithoutReceiver Arkade batch path");
        Assert.That(mock.SubmarineRefundRequestsFor(swapId), Is.GreaterThan(0),
            "SDK must have attempted the cooperative refund at least once before falling back");
    }
}
