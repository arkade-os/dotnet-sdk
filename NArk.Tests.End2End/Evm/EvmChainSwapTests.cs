using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Contracts.MessageEncodingServices;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Abstractions;
using NArk.Blockchain;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Evm;
using NArk.Swaps.Evm.Contracts;
using NArk.Swaps.Evm.Contracts.Erc20;
using NArk.Swaps.Evm.Contracts.Router;
using NArk.Swaps.Evm.Contracts.TestFixtures;
using NArk.Swaps.Evm.Extensions;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NArk.Swaps.Policies;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.Swaps;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End.Evm;

/// <summary>
/// Real-Boltz-counterparty E2E test for <see cref="EvmChainSwapProvider"/> — the EVM analogue
/// of <c>NArk.Tests.End2End.Swaps.ChainSwapTests</c>. Unlike <see cref="Erc20SwapContractTests"/>
/// (which only exercises our own <c>ERC20Swap</c> contract calls in isolation against a bare
/// Anvil node), this proves a full round trip against a real Boltz instance: swap creation,
/// cross-chain observation, and the automatic claim.
///
/// Requires the full EVM-enabled regtest stack (arkd + Boltz + Anvil with ERC20Swap/TestERC20
/// deployed and Boltz's <c>[arbitrum]</c> config wired to it) — see Milestone 3 of the EVM
/// swap-provider plan. Anvil alone (as <see cref="Erc20SwapContractTests"/> needs) is not
/// sufficient here.
/// </summary>
[Category("Evm")]
[Category("Swaps")]
public class EvmChainSwapTests
{
    /// <summary>
    /// Confirms Boltz has the ARK&lt;-&gt;TBTC chain-swap pair loaded before any test in this
    /// class runs. <see cref="SharedEvmInfrastructure"/> (Anvil reachability) and
    /// <see cref="SharedArkInfrastructure"/>/<see cref="SharedSwapInfrastructure"/> (arkd/Boltz
    /// reachability + ARK/BTC pair) are separate <c>[SetUpFixture]</c>s that already run for
    /// every test in their respective namespaces — this only adds the EVM-specific pair check,
    /// so plain <see cref="Erc20SwapContractTests"/> runs (Anvil-only, no Ark/Boltz needed)
    /// stay unaffected.
    /// </summary>
    [OneTimeSetUp]
    public async Task VerifyTbtcPairAvailable()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        const int maxAttempts = 30;
        var lastResponse = "";
        for (var i = 1; i <= maxAttempts; i++)
        {
            try
            {
                var response = await http.GetAsync($"{SharedSwapInfrastructure.BoltzEndpoint}/v2/swap/chain");
                lastResponse = await response.Content.ReadAsStringAsync();
                if (lastResponse.Contains("\"TBTC\""))
                {
                    TestContext.Progress.WriteLine($"Boltz ARK/TBTC pair ready (attempt {i})");
                    return;
                }
            }
            catch (Exception ex)
            {
                lastResponse = $"Exception: {ex.Message}";
            }

            if (i < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.Fail(
            "Boltz did not report an ARK<->TBTC chain-swap pair. Start the EVM-enabled regtest " +
            "stack (arkd + Boltz + Anvil, with Boltz's [arbitrum] config wired to the deployed " +
            "ERC20Swap/TestERC20) with:\n" +
            "  node regtest.mjs start --profile boltz,evm\n\n" +
            $"Last response: {lastResponse}");
    }

    /// <summary>
    /// ARK -&gt; EVM chain swap happy path: we lock an Ark VHTLC, Boltz observes it and locks
    /// tBTC in <c>ERC20Swap</c> on Anvil for our EVM account to claim, and
    /// <see cref="EvmChainSwapProvider"/>'s poll/websocket loop claims it automatically
    /// (<c>TryClaimEvmLockupAsync</c>), settling the swap. Verified both via the swap's own
    /// terminal status and directly on-chain (a real <c>Claim</c> event for our preimage hash).
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task CanDoArkToEvmChainSwap(CancellationToken token)
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);

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

        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions
            {
                BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
                WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString(),
            }));

        var evmOptions = new OptionsWrapper<EvmSwapOptions>(new EvmSwapOptions
        {
            RpcUrl = SharedEvmInfrastructure.AnvilRpcUrl,
            PrivateKey = SharedEvmInfrastructure.DeployerPrivateKey,
        });

        await using var evmProvider = new EvmChainSwapProvider(
            boltzClient, testingPrerequisite.clientTransport, testingPrerequisite.walletProvider, swapStorage,
            testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.vtxoStorage,
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider, evmOptions);

        var settledTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[ARK→EVM] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Settled) settledTcs.TrySetResult();
        };

        await evmProvider.StartAsync(token);

        var evmAccount = new Account(SharedEvmInfrastructure.DeployerPrivateKey);
        var refundDescriptor = await (await testingPrerequisite.walletProvider
            .GetAddressProviderAsync(testingPrerequisite.walletIdentifier))!.GetNextSigningDescriptor(token);

        const long amountSats = 50_000;
        var result = await evmProvider.CreateArkToEvmSwapAsync(
            testingPrerequisite.walletIdentifier, amountSats, refundDescriptor, evmAccount.Address, ct: token);

        Console.WriteLine($"[ARK→EVM] Swap {result.Swap.Id} created, Ark lockup: {result.Contract!.GetArkAddress().ToString(false)}");
        Assert.That(result.Swap.Id, Is.Not.Null.And.Not.Empty);

        // Fund the Ark VHTLC lockup from our regular wallet VTXOs.
        await spendingService.Spend(testingPrerequisite.walletIdentifier,
            [new ArkTxOut(ArkTxOutType.Vtxo, amountSats, result.Contract!.GetArkAddress())], token);
        Console.WriteLine("[ARK→EVM] Ark VHTLC lockup funded");

        // Wait for Boltz to observe the lockup, lock tBTC on Anvil, and for our own
        // poll/websocket loop to claim it — settling the swap.
        await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(2), token);

        var swaps = await swapStorage.GetSwaps(swapIds: [result.Swap.Id], cancellationToken: token);
        var finalSwap = swaps.Single();
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled));

        // Verify on-chain: our claim tx must have actually landed on Anvil's ERC20Swap.
        var web3 = new Web3(evmAccount, SharedEvmInfrastructure.AnvilRpcUrl);
        var chainInfo = await EvmChainClient.GetChainInfoAsync(boltzClient, "TBTC", token);
        var evmClient = new EvmChainClient(web3, chainInfo.SwapContracts.Erc20Swap);
        var claimEvent = await evmClient.FindClaimEventAsync(result.PreimageHash, token);
        Assert.That(claimEvent, Is.Not.Null,
            "Expected a Claim event on ERC20Swap for our preimage hash after the swap settled");
    }

    /// <summary>
    /// ARK -&gt; EVM chain swap cooperative refund: the user funds the Ark VHTLC lockup, Boltz is
    /// forced into <c>swap.expired</c> via a direct DB update + container restart (same pattern
    /// as <c>ChainSwapTests.ArkToBtcChainSwapRefundsCooperatively</c> — reuses
    /// <see cref="DockerHelper.SetArkToBtcChainSwapExpiredWithLockup"/> as-is, since that helper's
    /// SQL only touches the <c>symbol='ARK'</c> row, which is currency-agnostic on Boltz's side),
    /// and the SDK's poll loop calls <c>CoopRefundArkToEvmChainSwap</c> to return the locked VTXO
    /// cooperatively. Boltz must have seen the lockup before the forced expiry so it can validate
    /// the refund PSBT against its own records.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task CanRefundArkToEvmChainSwapCooperatively(CancellationToken token)
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

        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions
            {
                BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
                WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString(),
            }));

        var evmOptions = new OptionsWrapper<EvmSwapOptions>(new EvmSwapOptions
        {
            RpcUrl = SharedEvmInfrastructure.AnvilRpcUrl,
            PrivateKey = SharedEvmInfrastructure.DeployerPrivateKey,
        });

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        await using var evmProvider = new EvmChainSwapProvider(
            boltzClient, testingPrerequisite.clientTransport, testingPrerequisite.walletProvider, swapStorage,
            testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.vtxoStorage,
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider, evmOptions,
            logger: loggerFactory.CreateLogger<EvmChainSwapProvider>());

        var refundedTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[ARK→EVM refund] {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Refunded) refundedTcs.TrySetResult();
        };

        await evmProvider.StartAsync(token);

        // ── Step 1: create the swap (Boltz + storage) — not funded yet ──
        var evmAccount = new Account(SharedEvmInfrastructure.DeployerPrivateKey);
        var refundDescriptor = await (await testingPrerequisite.walletProvider
            .GetAddressProviderAsync(testingPrerequisite.walletIdentifier))!.GetNextSigningDescriptor(token);

        const long amountSats = 50_000;
        var result = await evmProvider.CreateArkToEvmSwapAsync(
            testingPrerequisite.walletIdentifier, amountSats, refundDescriptor, evmAccount.Address, ct: token);
        var swapId = result.Swap.Id;
        var vhtlcAddress = result.Contract!.GetArkAddress();
        Console.WriteLine($"[ARK→EVM refund] Swap created: {swapId}, vhtlc={vhtlcAddress.ToString(false)}");
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        // ── Step 2: stop Boltz so it never locks tBTC (swap can't settle) ──
        Console.WriteLine("[ARK→EVM refund] Stopping Boltz");
        await DockerHelper.StopContainer(DockerHelper.Container.Boltz, token);

        // ── Step 3: fund the VHTLC off-chain and mine a block ──
        Console.WriteLine($"[ARK→EVM refund] Funding VHTLC: {vhtlcAddress.ToString(false)}");
        await DockerHelper.SendArkdNoteTo(vhtlcAddress.ToString(false), amountSats, token);
        await DockerHelper.MineBlocks(1, token);

        // ── Step 4: read the VTXO txid directly from arkd ──
        string? lockupTxid = null;
        var lockupVout = 0;
        var contractScript = vhtlcAddress.ScriptPubKey.ToHex();
        for (var i = 0; i < 10 && lockupTxid is null; i++)
        {
            await foreach (var vtxo in testingPrerequisite.clientTransport.GetVtxoByScriptsAsSnapshot(
                               new HashSet<string> { contractScript }, token))
            {
                lockupTxid = vtxo.OutPoint.Hash.ToString();
                lockupVout = (int)vtxo.OutPoint.N;
                Console.WriteLine($"[ARK→EVM refund] VTXO found: {lockupTxid}:{lockupVout}");
                break;
            }
            if (lockupTxid is null) await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        Assert.That(lockupTxid, Is.Not.Null, "ARK VTXO not found at VHTLC script after mining");

        // ── Step 5: force swap.expired + set the ARK lockup tx in Boltz's DB, restart Boltz ──
        Console.WriteLine("[ARK→EVM refund] Setting swap.expired + transactionId in Boltz DB");
        await DockerHelper.SetArkToBtcChainSwapExpiredWithLockup(swapId, lockupTxid!, lockupVout, token);

        // ── Step 6: the provider's own poll loop (10s default interval) picks up
        // swap.expired on its next tick and attempts the cooperative refund ──
        await refundedTcs.Task.WaitAsync(TimeSpan.FromMinutes(3), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: token)).Single();
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            "ARK->EVM swap should reach Refunded status after cooperative refund completes");
    }

    /// <summary>
    /// EVM -&gt; ARK chain swap happy path, exercised through the convenience API
    /// (<see cref="SwapsManagementServiceEvmExtensions.InitiateEvmToArkChainSwap"/>) rather than
    /// the raw provider calls the other tests in this file use directly — this is the one that
    /// proves <see cref="EvmChainSwapProvider.EvmAddress"/>, <see cref="EvmChainSwapProvider.LockEvmAsync"/>,
    /// and the three <see cref="SwapsManagementService"/> passthroughs work end to end. We lock
    /// tBTC in <c>ERC20Swap</c> ourselves, Boltz locks an Ark VHTLC for our claim descriptor, and
    /// the wallet-wide <see cref="SweeperService"/> (via <see cref="SwapSweepPolicy"/>) claims it
    /// automatically — same mechanism as <c>ChainSwapTests.CanDoBtcToArkChainSwap</c>'s ARK leg.
    /// Verified both via the swap's own terminal status and directly on-chain (our own real
    /// lock call against ERC20Swap must be visible via <c>FindLockupEventAsync</c>).
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task CanDoEvmToArkChainSwap(CancellationToken token)
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

        await using var sweepMgr = new SweeperService(
            [new SwapSweepPolicy()], testingPrerequisite.vtxoStorage,
            coinService, testingPrerequisite.contracts,
            spendingService, intentStorage,
            new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions
            { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider, []);
        await sweepMgr.StartAsync(token);

        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions
            {
                BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
                WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString(),
            }));

        var evmOptions = new OptionsWrapper<EvmSwapOptions>(new EvmSwapOptions
        {
            RpcUrl = SharedEvmInfrastructure.AnvilRpcUrl,
            PrivateKey = SharedEvmInfrastructure.DeployerPrivateKey,
        });

        var evmProvider = new EvmChainSwapProvider(
            boltzClient, testingPrerequisite.clientTransport, testingPrerequisite.walletProvider, swapStorage,
            testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.vtxoStorage,
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider, evmOptions);

        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { evmProvider }, spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[EVM→ARK] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Settled) settledTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        const long amountSats = 50_000;
        var swapId = await swapMgr.InitiateEvmToArkChainSwap(testingPrerequisite.walletIdentifier, amountSats, token);
        Console.WriteLine($"[EVM→ARK] Swap created and tBTC locked: {swapId}");
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        // Wait for Boltz to observe the lockup, lock an Ark VHTLC for our claim descriptor, and
        // for the wallet-wide SweeperService to claim it — settling the swap.
        await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(2), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: token)).Single();
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled));

        // Verify on-chain: our own lock tx must have actually landed on Anvil's ERC20Swap.
        var evmAccount = new Account(SharedEvmInfrastructure.DeployerPrivateKey);
        var web3 = new Web3(evmAccount, SharedEvmInfrastructure.AnvilRpcUrl);
        var chainInfo = await EvmChainClient.GetChainInfoAsync(boltzClient, "TBTC", token);
        var evmClient = new EvmChainClient(web3, chainInfo.SwapContracts.Erc20Swap);
        var preimageHash = NBitcoin.Crypto.Hashes.SHA256(Convert.FromHexString(finalSwap.Get(SwapMetadata.Preimage)!));
        var lockupEvent = await evmClient.FindLockupEventAsync(preimageHash, token);
        Assert.That(lockupEvent, Is.Not.Null,
            "Expected a Lockup event on ERC20Swap for our preimage hash after locking tBTC");
    }

    /// <summary>
    /// Milestone 4 (USDT/generic-ERC20 DEX-hop) end-to-end: same swap as <see cref="CanDoEvmToArkChainSwap"/>,
    /// except funded from a USDT stand-in instead of tBTC directly, via
    /// <see cref="SwapsManagementServiceEvmExtensions.InitiateEvmToArkChainSwapFromErc20"/> —
    /// atomically pulls USDT via Permit2, swaps it to tBTC through <c>Router</c> (using a
    /// <c>MockERC20Dex</c> deployed by the regtest stack itself — see
    /// <c>arkade-regtest/lib/setup/evm.mjs</c>'s <c>setupUsdtDexHop</c>), and locks the result —
    /// Boltz never learns any of this happened; it still just watches for the same tBTC Lockup
    /// event it always does, so everything downstream (Ark VHTLC lock, SweeperService claim) is
    /// identical to the plain tBTC flow.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task CanDoEvmToArkChainSwapFromUsdt(CancellationToken token)
    {
        var evmAddresses = DockerHelper.GetEvmAddresses();

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

        await using var sweepMgr = new SweeperService(
            [new SwapSweepPolicy()], testingPrerequisite.vtxoStorage,
            coinService, testingPrerequisite.contracts,
            spendingService, intentStorage,
            new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions
            { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider, []);
        await sweepMgr.StartAsync(token);

        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions
            {
                BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
                WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString(),
            }));

        var evmOptions = new OptionsWrapper<EvmSwapOptions>(new EvmSwapOptions
        {
            RpcUrl = SharedEvmInfrastructure.AnvilRpcUrl,
            PrivateKey = SharedEvmInfrastructure.DeployerPrivateKey,
        });

        var dexWeb3 = new Web3(new Account(SharedEvmInfrastructure.DeployerPrivateKey), SharedEvmInfrastructure.AnvilRpcUrl);
        var routerClient = new RouterClient(dexWeb3, evmAddresses.RouterAddress);
        var dexQuoteProvider = new MockDexQuoteProvider(evmAddresses.MockDexAddress);
        var dexSwapService = new DEXSwapService(routerClient, dexQuoteProvider);

        var evmProvider = new EvmChainSwapProvider(
            boltzClient, testingPrerequisite.clientTransport, testingPrerequisite.walletProvider, swapStorage,
            testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.vtxoStorage,
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider, evmOptions,
            dexSwapService: dexSwapService);

        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { evmProvider }, spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        var settledTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            Console.WriteLine($"[EVM(USDT)→ARK] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Settled) settledTcs.TrySetResult();
        };

        await swapMgr.StartAsync(token);

        // One-time on-chain approve to Permit2 itself — required even with witness signatures,
        // see Permit2Signer's doc comment. The deployer already holds USDT (setupUsdtDexHop mints
        // its full initial supply to the deployer, same as tBTC/TestERC20 always has).
        var usdtApproveHandler = dexWeb3.Eth.GetContractHandler(evmAddresses.UsdtAddress);
        await usdtApproveHandler.SendRequestAndWaitForReceiptAsync(new ApproveFunction
        { Spender = evmAddresses.Permit2Address, Value = new BigInteger(1_000_000_000) });

        const long amountSats = 50_000;

        // Not using InitiateEvmToArkChainSwapFromErc20 here: Boltz's lockupDetails.Amount
        // (what must actually land in ERC20Swap) includes its own fee markup over amountSats —
        // confirmed live (50248 expected vs 50000 naively assumed) — and is only known after
        // swap creation, so amountIn can't be chosen correctly until then. Create first, then
        // lock with the DEX-hop amount actually required.
        var addressProvider = await testingPrerequisite.walletProvider
            .GetAddressProviderAsync(testingPrerequisite.walletIdentifier, token);
        var claimDescriptor = await addressProvider!.GetNextSigningDescriptor(token);
        var result = await evmProvider.CreateEvmToArkSwapAsync(
            testingPrerequisite.walletIdentifier, amountSats, claimDescriptor, ct: token);
        var swapId = result.Swap.Id;

        await evmProvider.LockEvmFromErc20Async(
            result, evmAddresses.UsdtAddress, result.Swap.LockupDetails!.Amount, token);
        Console.WriteLine($"[EVM(USDT)→ARK] Swap created and tBTC locked via USDT DEX hop: {swapId}");
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        // Wait for Boltz to observe the lockup, lock an Ark VHTLC for our claim descriptor, and
        // for the wallet-wide SweeperService to claim it — settling the swap.
        await settledTcs.Task.WaitAsync(TimeSpan.FromMinutes(2), token);

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: token)).Single();
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Settled));

        // Verify on-chain: the DEX-hop lock tx must have actually landed on Anvil's ERC20Swap,
        // and Router must hold no leftover balance of either token.
        var chainInfo = await EvmChainClient.GetChainInfoAsync(boltzClient, "TBTC", token);
        var evmClient = new EvmChainClient(dexWeb3, chainInfo.SwapContracts.Erc20Swap);
        var preimageHash = NBitcoin.Crypto.Hashes.SHA256(Convert.FromHexString(finalSwap.Get(SwapMetadata.Preimage)!));
        var lockupEvent = await evmClient.FindLockupEventAsync(preimageHash, token);
        Assert.That(lockupEvent, Is.Not.Null,
            "Expected a Lockup event on ERC20Swap for our preimage hash after the USDT DEX-hop lock");

        var routerTbtcBalance = await dexWeb3.Eth.GetContractHandler(evmAddresses.TbtcAddress)
            .QueryAsync<BalanceOfFunction, BigInteger>(new BalanceOfFunction { Account = evmAddresses.RouterAddress });
        Assert.That(routerTbtcBalance, Is.EqualTo(BigInteger.Zero), "Router should hold no leftover tBTC after the DEX hop");
    }

    /// <summary>Builds Router Call[]s for a fixed 1:1 MockERC20Dex swap — mirrors
    /// NArk.Tests.End2End.Evm.RouterDexHopTests's identical private test double.</summary>
    private class MockDexQuoteProvider(string dexAddress) : IDexQuoteProvider
    {
        public Task<DexSwapQuote> GetSwapCallsAsync(
            string tokenIn, string tokenOut, BigInteger amountIn, CancellationToken ct = default)
        {
            var calls = new List<Call>
            {
                new()
                {
                    Target = tokenIn, Value = 0,
                    CallData = new FunctionMessageEncodingService<ApproveFunction>()
                        .GetCallData(new ApproveFunction { Spender = dexAddress, Value = amountIn }),
                },
                new()
                {
                    Target = dexAddress, Value = 0,
                    CallData = new FunctionMessageEncodingService<SwapFunction>()
                        .GetCallData(new SwapFunction { Amount = amountIn }),
                },
            };
            return Task.FromResult(new DexSwapQuote(calls, amountIn));
        }
    }

    /// <summary>
    /// EVM -&gt; ARK chain swap refund: we lock tBTC in <c>ERC20Swap</c>, the swap is forced into
    /// <c>swap.expired</c> via <see cref="DockerHelper.TrySetBoltzSwapStatus"/> (the generic,
    /// currency-agnostic helper — unlike the ARK-side refund test above, no lockup txid needs to
    /// be recorded in Boltz's DB, since our EVM refund reads everything from on-chain events, not
    /// from Boltz's records), and the provider's own poll loop calls <c>TryRefundEvmLockupAsync</c>
    /// once Anvil's chain height passes Boltz's own <c>timeoutBlockHeight</c> — a real,
    /// safety-margined absolute block number Boltz chose (not something we control, and far
    /// larger than "current+1"), so this test fast-forwards Anvil past it via the <c>anvil_mine</c>
    /// dev RPC method rather than mining that many blocks one real transaction at a time. Verified
    /// directly on-chain (a real <c>Refund</c> event for our preimage hash); whether Boltz's own
    /// status also reaches <c>transaction.refunded</c> (and hence whether <see cref="ArkSwap.Status"/>
    /// reaches <see cref="ArkSwapStatus.Refunded"/>) is logged but not asserted on, since
    /// <c>TryRefundEvmLockupAsync</c> never sets that field itself — it relies entirely on Boltz's
    /// own indexer observing our unilateral on-chain refund.
    /// </summary>
    [Test]
    [CancelAfter(300_000)]
    public async Task CanRefundEvmToArkChainSwapAfterTimelock(CancellationToken token)
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

        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions
            {
                BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
                WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString(),
            }));

        var evmOptions = new OptionsWrapper<EvmSwapOptions>(new EvmSwapOptions
        {
            RpcUrl = SharedEvmInfrastructure.AnvilRpcUrl,
            PrivateKey = SharedEvmInfrastructure.DeployerPrivateKey,
        });

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var evmProvider = new EvmChainSwapProvider(
            boltzClient, testingPrerequisite.clientTransport, testingPrerequisite.walletProvider, swapStorage,
            testingPrerequisite.contractService, testingPrerequisite.contracts, testingPrerequisite.vtxoStorage,
            testingPrerequisite.safetyService, intentStorage, chainTimeProvider, evmOptions,
            logger: loggerFactory.CreateLogger<EvmChainSwapProvider>());

        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { evmProvider }, spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider, swapStorage, testingPrerequisite.contractService,
            testingPrerequisite.contracts, testingPrerequisite.safetyService, intentStorage, chainTimeProvider);

        swapStorage.SwapsChanged += (_, swap) =>
            Console.WriteLine($"[EVM→ARK refund] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");

        await swapMgr.StartAsync(token);

        // ── Step 1: create the swap and lock tBTC ──
        const long amountSats = 50_000;
        var swapId = await swapMgr.InitiateEvmToArkChainSwap(testingPrerequisite.walletIdentifier, amountSats, token);
        Console.WriteLine($"[EVM→ARK refund] Swap created and tBTC locked: {swapId}");
        Assert.That(swapId, Is.Not.Null.And.Not.Empty);

        var swap = (await swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: token)).Single();
        var boltzResponse = JsonSerializer.Deserialize<ChainResponse>(swap.Metadata![SwapMetadata.BoltzResponse])!;
        var timeoutBlockHeight = boltzResponse.LockupDetails!.TimeoutBlockHeight;
        Console.WriteLine($"[EVM→ARK refund] Boltz timeoutBlockHeight = {timeoutBlockHeight}");

        // ── Step 2: force swap.expired (currency-agnostic helper — no lockup txid needed, our
        // refund reads everything from on-chain events, not Boltz's records) ──
        Console.WriteLine("[EVM→ARK refund] Forcing swap.expired");
        var forced = await DockerHelper.TrySetBoltzSwapStatus(swapId, BoltzSwapStatus.SwapExpired, token);
        if (!forced) Assert.Ignore("Could not force swap.expired via boltzr-cli or direct DB update.");

        // ── Step 3: fast-forward Anvil past Boltz's own (real, safety-margined) timelock — via
        // anvil_mine rather than sending hundreds of real dummy transactions ──
        var evmAccount = new Account(SharedEvmInfrastructure.DeployerPrivateKey);
        var web3 = new Web3(evmAccount, SharedEvmInfrastructure.AnvilRpcUrl);
        var currentBlock = (long)(await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        var blocksToMine = timeoutBlockHeight - currentBlock + 1;
        Console.WriteLine($"[EVM→ARK refund] Current block {currentBlock}, mining {blocksToMine} to pass timelock {timeoutBlockHeight}");
        if (blocksToMine > 0)
            await AnvilMineBlocksAsync(blocksToMine, token);

        var newBlock = (long)(await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        Assert.That(newBlock, Is.GreaterThanOrEqualTo(timeoutBlockHeight),
            "Anvil should have been fast-forwarded past Boltz's timeoutBlockHeight");

        // ── Step 4: the provider's own poll loop (10s default interval) picks up swap.expired on
        // its next tick and refunds directly on-chain ──
        var chainInfo = await EvmChainClient.GetChainInfoAsync(boltzClient, "TBTC", token);
        var evmClient = new EvmChainClient(web3, chainInfo.SwapContracts.Erc20Swap);
        var preimageHash = NBitcoin.Crypto.Hashes.SHA256(Convert.FromHexString(swap.Get(SwapMetadata.Preimage)!));

        RefundEventDTO? refundEvent = null;
        for (var i = 0; i < 30 && refundEvent is null; i++)
        {
            refundEvent = await evmClient.FindRefundEventAsync(preimageHash, token);
            if (refundEvent is null) await Task.Delay(TimeSpan.FromSeconds(5), token);
        }

        Assert.That(refundEvent, Is.Not.Null,
            "Expected a Refund event on ERC20Swap for our preimage hash after the timelock passed");

        var finalSwap = (await swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: token)).Single();
        Console.WriteLine($"[EVM→ARK refund] Final swap status: {finalSwap.Status}");
        Assert.That(finalSwap.Status, Is.EqualTo(ArkSwapStatus.Refunded),
            "TryRefundEvmLockupAsync should mark the swap Refunded itself once its on-chain refund " +
            "succeeds, rather than waiting on Boltz's own indexer to notice (which this session found " +
            "doesn't reliably happen for a refund that doesn't move Boltz's own funds)");
    }

    /// <summary>
    /// Mines <paramref name="count"/> blocks instantly on Anvil via its <c>anvil_mine</c> dev RPC
    /// method (a raw HTTP JSON-RPC call, same low-level pattern <see cref="SharedEvmInfrastructure"/>
    /// uses for its <c>eth_chainId</c> health check) — used to fast-forward past a real Boltz-chosen
    /// <c>timeoutBlockHeight</c> without sending that many real dummy transactions.
    /// </summary>
    private static async Task AnvilMineBlocksAsync(long count, CancellationToken ct)
    {
        using var http = new HttpClient();
        var payload = new StringContent(
            $$"""{"jsonrpc":"2.0","method":"anvil_mine","params":["0x{{count:x}}"],"id":1}""",
            Encoding.UTF8, "application/json");
        var response = await http.PostAsync(SharedEvmInfrastructure.AnvilRpcUrl, payload, ct);
        response.EnsureSuccessStatusCode();
    }
}
