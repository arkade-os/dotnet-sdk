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
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Evm;
using NArk.Swaps.Evm.Extensions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.Mocks;
using NArk.Tests.End2End.Swaps;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End.Evm;

/// <summary>
/// EVM counterpart of <see cref="MockBoltzChainUnilateralTests.ChainArkToBtc_WhenBoltzRefusesCoop"/>:
/// same scenario (Boltz permanently refuses the cooperative ARK refund co-sign, so the SDK must
/// fall through to <c>TryRefundWithoutReceiverAsync</c>'s Arkade batch path), but for
/// <see cref="ArkSwapType.ChainArkToEvm"/> via <see cref="EvmChainSwapProvider"/> instead of
/// <c>BoltzSwapProvider</c>'s <c>ChainArkToBtc</c>. Uses <see cref="MockBoltzServer"/> rather than
/// a live regtest Boltz because this scenario needs deterministic control over the refund
/// co-sign (always fail) and the VHTLC's <c>RefundLocktime</c> (mock sets a long-past timestamp)
/// — impractical to force reliably against a live counterpart. The EVM leg (Anvil/ERC20Swap) is
/// never touched at all here: <c>ChainArkToEvm</c>'s refund-without-receiver path only concerns
/// our own ARK-side VHTLC lockup, which Boltz was meant to claim but never will — the funds never
/// leave Arkade.
///
/// Note: there is no equivalent "coop-fails-then-fallback" scenario for the <c>ChainEvmToArk</c>
/// direction — the EVM lockup's only refund mechanism is <c>ERC20Swap.refund()</c>, gated purely
/// by an on-chain timelock with no counterparty co-signature involved at all, so it is already
/// unconditionally unilateral. That path is covered live against a real Boltz+Anvil by
/// <see cref="EvmChainSwapTests.CanRefundEvmToArkChainSwapAfterTimelock"/>.
/// </summary>
[Category("Evm")]
[Category("Swaps")]
[NonParallelizable]
public class MockBoltzEvmChainUnilateralTests
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
        var blockchain = new NBXplorerBlockchain(
            Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);

        var evmOptions = new OptionsWrapper<EvmSwapOptions>(new EvmSwapOptions
        {
            RpcUrl = SharedEvmInfrastructure.AnvilRpcUrl,
            PrivateKey = SharedEvmInfrastructure.DeployerPrivateKey,
        });

        var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Debug));
        var evmProvider = new EvmChainSwapProvider(
            boltzClient, clientTransport, walletProvider, swapStorage,
            contractService, contracts, vtxoStorage, safetyService, intentStorage, blockchain,
            evmOptions, intentGenerationService, loggerFactory.CreateLogger<EvmChainSwapProvider>());

        return new SwapsManagementService(
            new ISwapProvider[] { evmProvider },
            new SpendingService(vtxoStorage, contracts, walletProvider,
                new CoinService(clientTransport, contracts,
                [
                    new PaymentContractTransformer(walletProvider),
                    new HashLockedContractTransformer(walletProvider),
                    new VHTLCContractTransformer(walletProvider, blockchain)
                ]),
                contractService, clientTransport, new DefaultCoinSelector(), safetyService, intentStorage),
            clientTransport, vtxoStorage,
            walletProvider, swapStorage, contractService,
            contracts, safetyService, intentStorage, blockchain);
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

    /// <summary>
    /// Boltz refuses every cooperative ARK refund co-sign (<c>RefundMode.Fail</c>) after the
    /// <c>ChainArkToEvm</c> swap expires. Once the VHTLC's <c>RefundLocktime</c> (a long-past
    /// timestamp, set by <see cref="MockBoltzServer"/>'s <c>DefaultTimeouts</c>) has elapsed, the
    /// SDK must fall through from <c>CoopRefundArkToEvmChainSwap</c> to
    /// <c>TryRefundWithoutReceiverAsync</c>, which joins an Arkade batch session using the
    /// <c>refundWithoutReceiver</c> tapscript path (server + sender, absolute CLTV). The swap
    /// must reach <see cref="ArkSwapStatus.Refunded"/> without ever touching the EVM leg at all.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task ChainArkToEvm_WhenBoltzRefusesCoop(CancellationToken token)
    {
        Console.WriteLine("[DIAG] starting mock server");
        await using var mock = await MockBoltzServer.StartAsync();
        mock.SetRefundMode(RefundMode.Fail);
        Console.WriteLine("[DIAG] mock server started, getting funded wallet");

        var prereq = await FundedWalletHelper.GetFundedWallet();
        Console.WriteLine("[DIAG] funded wallet ready");
        mock.ServerInfo = await prereq.clientTransport.GetServerInfoAsync();
        Console.WriteLine("[DIAG] server info fetched");

        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();

        // Wire up the full batch stack so TryRefundWithoutReceiverAsync can submit the VHTLC
        // spend as a manual batch intent — mirrors MockBoltzChainUnilateralTests.ChainArkToBtc_WhenBoltzRefusesCoop.
        var blockchain = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);

        var coinService = new CoinService(prereq.clientTransport, prereq.contracts,
        [
            new PaymentContractTransformer(prereq.walletProvider),
            new HashLockedContractTransformer(prereq.walletProvider),
            new VHTLCContractTransformer(prereq.walletProvider, blockchain)
        ]);

        // Coin service WITHOUT VHTLCContractTransformer for IntentGenerationService — it must
        // not auto-sweep the VHTLC VTXO, since TryRefundWithoutReceiverAsync owns that path via
        // GenerateManualIntent (racing both would hit VTXO_ALREADY_REGISTERED on arkd).
        var coinServiceForIntentGen = new CoinService(prereq.clientTransport, prereq.contracts,
        [
            new PaymentContractTransformer(prereq.walletProvider),
            new HashLockedContractTransformer(prereq.walletProvider),
        ]);

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(prereq.clientTransport, blockchain),
            prereq.clientTransport, prereq.contractService, blockchain,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(
                new SimpleIntentSchedulerOptions { Threshold = TimeSpan.FromHours(24), ThresholdHeight = 100_000 }));

        var intentGeneration = new IntentGenerationService(
            prereq.clientTransport,
            new DefaultFeeEstimator(prereq.clientTransport, blockchain),
            coinServiceForIntentGen, prereq.walletProvider, intentStorage,
            prereq.safetyService, prereq.contracts, prereq.vtxoStorage, scheduler,
            new OptionsWrapper<IntentGenerationServiceOptions>(new IntentGenerationServiceOptions
                { PollInterval = TimeSpan.FromHours(5) }));

        await using var intentSync = new IntentSynchronizationService(
            intentStorage, prereq.clientTransport, prereq.safetyService);

        await using var batchManager = new BatchManagementService(
            intentStorage, prereq.clientTransport, prereq.vtxoStorage,
            prereq.contracts, prereq.walletProvider, coinService, prereq.safetyService);

        await using var _ = intentGeneration;

        await using var swapMgr = BuildSwapMgr(mock,
            prereq.safetyService, prereq.walletProvider, prereq.vtxoStorage,
            prereq.contractService, prereq.contracts, prereq.clientTransport,
            swapStorage, intentStorage, intentGeneration);

        var refundedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[ArkToEvmRefund] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.Cancelled)
                Console.WriteLine($"[Intent] CANCELLED {intent.IntentTxId}: {intent.CancellationReason}");
            else
                Console.WriteLine($"[Intent] {intent.IntentTxId} → {intent.State}");
        };

Console.WriteLine("[DIAG] batch stack built, starting swapMgr");
        await swapMgr.StartAsync(token);
        Console.WriteLine("[DIAG] swapMgr started, initiating swap");

        // InitiateArkToEvmChainSwap creates the swap AND funds the ARK VHTLC lockup itself
        // (unlike the raw provider call) — exercises the Task 3 convenience API end to end here too.
        var swapId = await swapMgr.InitiateArkToEvmChainSwap(prereq.walletIdentifier, 50_000, token);
        Console.WriteLine($"[ArkToEvmRefund] Swap {swapId} created and VHTLC funding submitted");

        var arkSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();

        Console.WriteLine("[ArkToEvmRefund] Waiting for VTXO at swap script...");
        await WaitForVtxoAtScript(prereq.vtxoStorage, arkSwap.ContractScript, arkSwap.ExpectedAmount, token);
        Console.WriteLine("[ArkToEvmRefund] VTXO at swap script confirmed");

        // Start the batch stack only after the VHTLC VTXO is confirmed — starting earlier would
        // race IntentGenerationService's first sweep cycle against SpendingService.Spend.
        await intentGeneration.StartAsync(token);
        await intentSync.StartAsync(token);
        await batchManager.StartAsync(token);

        // RefundLocktime is a past Unix timestamp (mock's DefaultTimeouts), already elapsed —
        // just nudge arkd into its next batch session.
        await DockerHelper.MineBlocks(1, token);
        Console.WriteLine("[ArkToEvmRefund] Mined 1 block to nudge next Arkade batch");

        // Push swap.expired — triggers TryCoopRefundArkToEvm → coop fails (RefundMode.Fail) →
        // RefundLocktime elapsed → TryRefundWithoutReceiverAsync joins the Arkade batch.
        await mock.PushSwapEvent(swapId, "swap.expired", token);
        Console.WriteLine("[ArkToEvmRefund] Pushed swap.expired");

        await refundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(120), token);

        var final = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(final.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            "Swap must reach Refunded via the refundWithoutReceiver Arkade batch path");
        Assert.That(mock.ChainArkRefundRequestsFor(swapId), Is.GreaterThan(0),
            "SDK must have attempted the cooperative ARK refund at least once");
    }
}
