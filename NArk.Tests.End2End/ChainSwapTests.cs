using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NArk.Swaps.Policies;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;
using NBitcoin;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End.Swaps;

public class ChainSwapTests
{
    private DistributedApplication _app;

    [OneTimeSetUp]
    public async Task StartDependencies()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: [],
                configureBuilder: (appOptions, _) => { appOptions.AllowUnsecuredTransport = true; }
            );

        _app = await builder.BuildAsync();
        await _app.StartAsync(CancellationToken.None);
        var waitForBoltzHealthTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("boltz", waitForBoltzHealthTimeout.Token);

        // Ensure Bitcoin Core default wallet has mature coinbase funds for Boltz chain swaps.
        // Mine enough blocks so coinbase outputs pass the 100-block maturity requirement.
        for (var i = 0; i < 6; i++)
            await _app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
        await Task.Delay(TimeSpan.FromSeconds(5));
    }

    [OneTimeTearDown]
    public async Task StopDependencies()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Test]
    public async Task CanDoBtcToArkChainSwap()
    {
        var boltzProxy = _app.GetEndpoint("boltz-proxy", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http"));
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzProxy.ToString(), WebsocketUrl = boltzWs.ToString() }));
        var intentStorage = new InMemoryIntentStorage();

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
            spendingService,
            new OptionsWrapper<SweeperServiceOptions>(new SweeperServiceOptions()
            { ForceRefreshInterval = TimeSpan.Zero }), chainTimeProvider);
        await sweepMgr.StartAsync(CancellationToken.None);

        await using var swapMgr = new SwapsManagementService(
            spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, intentStorage, boltzClient, chainTimeProvider);

        var settledSwapTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (sender, swap) =>
        {
            Console.WriteLine($"[BTC→ARK] SwapsChanged: {swap.SwapId} → {swap.Status} (fail: {swap.FailReason})");
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);

        // Create BTC→ARK chain swap
        var (btcAddress, swapId, expectedLockupSats) = await swapMgr.InitiateBtcToArkChainSwap(
            testingPrerequisite.walletIdentifier,
            50000,
            CancellationToken.None
        );

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
            await _app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");

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
        var boltzProxy = _app.GetEndpoint("boltz-proxy", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http"));
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzProxy.ToString(), WebsocketUrl = boltzWs.ToString() }));
        var intentStorage = new InMemoryIntentStorage();

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

        await using var swapMgr = new SwapsManagementService(
            spendingService,
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.walletProvider,
            swapStorage, testingPrerequisite.contractService, testingPrerequisite.contracts,
            testingPrerequisite.safetyService, intentStorage, boltzClient, chainTimeProvider);

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
            await _app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");

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
}
