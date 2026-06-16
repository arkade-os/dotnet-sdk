using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Exit;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.VirtualTxs;
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
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End.Swaps;

/// <summary>
/// Chain swap unilateral exit scenarios exercised with the in-process
/// <see cref="MockBoltzServer"/>: BTC→ARK CLTV script-path refund and
/// ARK→BTC VHTLC unilateral Arkade exit when Boltz permanently refuses
/// the cooperative co-sign.
/// </summary>
[NonParallelizable]
public class MockBoltzChainUnilateralTests
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
        NArk.Abstractions.Intents.IIntentStorage intentStorage,
        UnilateralExitService? unilateralExitService = null)
    {
        var opts = new BoltzClientOptions
            { BoltzUrl = mock.BaseUrl, WebsocketUrl = mock.WsBaseUrl };
        var optsWrapper = new OptionsWrapper<BoltzClientOptions>(opts);
        var boltzClient = new BoltzClient(new HttpClient(), optsWrapper);
        var blockchain = new NBXplorerBlockchain(
            Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);

        var coinService = new CoinService(clientTransport, contracts,
        [
            new PaymentContractTransformer(walletProvider),
            new HashLockedContractTransformer(walletProvider),
            new VHTLCContractTransformer(walletProvider, blockchain)
        ]);
        var spendingService = new SpendingService(
            vtxoStorage, contracts, walletProvider,
            coinService, contractService, clientTransport,
            new DefaultCoinSelector(), safetyService, intentStorage);
        var boltzProvider = new BoltzSwapProvider(
            boltzClient,
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), optsWrapper)),
            clientTransport, vtxoStorage, walletProvider,
            swapStorage, contractService, contracts,
            safetyService, intentStorage, blockchain,
            unilateralExitService);

        return new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, clientTransport, vtxoStorage,
            walletProvider, swapStorage, contractService,
            contracts, safetyService, intentStorage, blockchain);
    }

    private static UnilateralExitService BuildUnilateralExitService(
        NArk.Core.Transport.IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IContractStorage contracts,
        IWalletProvider walletProvider)
    {
        var virtualTxStorage = new InMemoryVirtualTxStorage();
        var exitSessionStorage = new InMemoryExitSessionStorage();
        var blockchain = new NBXplorerBlockchain(
            Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var virtualTxService = new VirtualTxService(clientTransport, virtualTxStorage);
        return new UnilateralExitService(
            clientTransport, virtualTxStorage, exitSessionStorage,
            vtxoStorage, contracts, blockchain,
            walletProvider, virtualTxService);
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

    // ── Test 1: BTC→ARK unilateral CLTV refund ───────────────────────

    /// <summary>
    /// Boltz refuses every cooperative BTC refund co-sign (<c>RefundMode.Fail</c>)
    /// and the CLTV timelock (block 144) has elapsed. The SDK must fall through
    /// from <c>CoopRefundBtcToArkChainSwap</c> to the script-path unilateral spend
    /// (<c>UnilateralRefundBtcToArkChainSwap</c>), broadcast via
    /// <c>POST /v2/chain/BTC/transaction</c>, and transition the swap to
    /// <see cref="ArkSwapStatus.Refunded"/>.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task ChainBtcToArk_UnilateralCltvRefund_WhenBoltzRefusesCoop(CancellationToken token)
    {
        await using var mock = await MockBoltzServer.StartAsync();
        mock.SetRefundMode(RefundMode.Fail);

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
            Console.WriteLine($"[BtcToArkCltv] {swap.SwapId} → {swap.Status}");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var (btcAddress, swapId, expectedSats) =
            await swapMgr.InitiateBtcToArkChainSwap(prereq.walletIdentifier, 50_000, token);
        Console.WriteLine($"[BtcToArkCltv] Swap {swapId} created, BTC lockup: {btcAddress} ({expectedSats} sat)");

        // Provide the mock server with a lockup tx so GetSwapStatusAsync returns hex.
        // The tx only needs an output at btcAddress; the mock's broadcast endpoint
        // accepts any hex and returns a synthetic txid without touching L1.
        var serverInfo = await prereq.clientTransport.GetServerInfoAsync(token);
        var fakeLockupTx = serverInfo.Network.CreateTransaction();
        fakeLockupTx.Outputs.Add(
            Money.Satoshis(expectedSats),
            BitcoinAddress.Create(btcAddress, serverInfo.Network));
        mock.SetLockupTxHex(swapId, fakeLockupTx.ToHex());
        Console.WriteLine($"[BtcToArkCltv] Lockup tx set (fake txid={fakeLockupTx.GetHash()})");

        // Mine past the CLTV timeout (MockBoltzServer hardcodes btcTimeout=144).
        await DockerHelper.MineRegtestBlocksToHeight(145, token);
        Console.WriteLine("[BtcToArkCltv] Mined to height 145 (past CLTV timeout 144)");

        // Push swap.expired — triggers TryRefundBtcToArk in the SDK poll loop.
        await mock.PushSwapEvent(swapId, "swap.expired", token);
        Console.WriteLine("[BtcToArkCltv] Pushed swap.expired");

        await refundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);

        var final = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(final.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            "Swap must reach Refunded via the unilateral CLTV script-path");
        Assert.That(mock.ChainBtcRefundRequestsFor(swapId), Is.GreaterThan(0),
            "SDK must have attempted the cooperative BTC refund at least once");
    }

    // ── Test 2: ARK→BTC unilateral VHTLC exit ────────────────────────

    /// <summary>
    /// Boltz refuses every cooperative ARK refund co-sign (<c>RefundMode.Fail</c>)
    /// after the swap expires. The SDK must fall through from
    /// <c>CoopRefundArkToBtcChainSwap</c> to the unilateral Arkade exit via
    /// <see cref="UnilateralExitService"/>, mark the swap
    /// <see cref="ArkSwapStatus.Refunded"/>, and the exit session must be
    /// persisted so <c>ProgressExitsAsync</c> can later advance it through the
    /// CSV wait and on-chain claim.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task ChainArkToBtc_UnilateralVhtlcExit_WhenBoltzRefusesCoop(CancellationToken token)
    {
        await using var mock = await MockBoltzServer.StartAsync();
        mock.SetRefundMode(RefundMode.Fail);

        var prereq = await FundedWalletHelper.GetFundedWallet();
        mock.ServerInfo = await prereq.clientTransport.GetServerInfoAsync();

        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        var unilateralExit = BuildUnilateralExitService(
            prereq.clientTransport, prereq.vtxoStorage,
            prereq.contracts, prereq.walletProvider);

        await using var swapMgr = BuildSwapMgr(mock,
            prereq.safetyService, prereq.walletProvider, prereq.vtxoStorage,
            prereq.contractService, prereq.contracts, prereq.clientTransport,
            swapStorage, intentStorage,
            unilateralExitService: unilateralExit);

        var refundedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[ArkToBtcVhtlc] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        var serverInfo = await prereq.clientTransport.GetServerInfoAsync(token);
        var btcDest = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, serverInfo.Network);

        var swapId = await swapMgr.InitiateArkToBtcChainSwap(
            prereq.walletIdentifier, 50_000, btcDest, token);
        Console.WriteLine($"[ArkToBtcVhtlc] Swap {swapId} created");

        var arkSwap = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();

        Console.WriteLine("[ArkToBtcVhtlc] Waiting for VTXO at swap script...");
        await WaitForVtxoAtScript(prereq.vtxoStorage, arkSwap.ContractScript, arkSwap.ExpectedAmount, token);
        Console.WriteLine("[ArkToBtcVhtlc] VTXO at swap script confirmed");

        // Push swap.expired — triggers TryCoopRefundArkToBtc → coop fails →
        // TryStartVhtlcUnilateralExitAsync fires since UnilateralExitService is registered.
        await mock.PushSwapEvent(swapId, "swap.expired", token);
        Console.WriteLine("[ArkToBtcVhtlc] Pushed swap.expired");

        await refundedTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);

        var final = (await swapStorage.GetSwaps(swapIds: [swapId])).Single();
        Assert.That(final.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            "Swap must reach Refunded after unilateral VHTLC exit is triggered");
        Assert.That(mock.ChainArkRefundRequestsFor(swapId), Is.GreaterThan(0),
            "SDK must have attempted the cooperative ARK refund at least once");
    }
}
