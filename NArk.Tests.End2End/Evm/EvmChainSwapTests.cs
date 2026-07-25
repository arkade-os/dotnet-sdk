using Microsoft.Extensions.Options;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Abstractions;
using NArk.Blockchain;
using NArk.Core.CoinSelector;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Evm;
using NArk.Swaps.Models;
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
            boltzClient, testingPrerequisite.clientTransport, swapStorage,
            testingPrerequisite.contractService, evmOptions);

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
}
