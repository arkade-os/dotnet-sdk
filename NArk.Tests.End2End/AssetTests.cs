using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Core.CoinSelector;
using NArk.Core.Events;
using NArk.Core.Extensions;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NArk.Safety.AsyncKeyedLock;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace NArk.Tests.End2End.Core;

[TestFixture]
public class AssetTests
{
    [Test, Order(1)]
    public async Task CanIssueAsset()
    {
        var app = SharedArkInfrastructure.App;
        var walletDetails = await FundedWalletHelper.GetFundedWallet(app);

        var (assetManager, _, _) = CreateAssetServices(walletDetails);

        // Issue 1000 units of a new asset
        var result = await assetManager.IssueAsync(walletDetails.walletIdentifier,
            new IssuanceParams(Amount: 1000));

        Assert.That(result.AssetId, Is.Not.Null.And.Not.Empty, "AssetId should be non-empty");
        Assert.That(result.ArkTxId, Is.Not.Null.And.Not.Empty, "ArkTxId should be non-empty");

        // Poll until VTXO with asset appears (server needs time to index after SubmitTx)
        await PollUntilAssetVtxo(walletDetails, result.AssetId, TimeSpan.FromSeconds(30));

        // Verify via GetAssetDetailsAsync
        var details = await walletDetails.clientTransport.GetAssetDetailsAsync(result.AssetId);
        Assert.That(details.AssetId, Is.EqualTo(result.AssetId));
        Assert.That(details.Supply, Is.EqualTo(1000UL));
    }

    [Test, Order(2)]
    public async Task CanTransferAssetBetweenWallets()
    {
        var app = SharedArkInfrastructure.App;

        // Fund Alice
        var alice = await FundedWalletHelper.GetFundedWallet(app);
        // Fund Bob
        var bob = await FundedWalletHelper.GetFundedWallet(app);

        var (aliceAssetManager, aliceCoinService, aliceIntentStorage) = CreateAssetServices(alice);

        // Alice issues 1000 units
        var issuance = await aliceAssetManager.IssueAsync(alice.walletIdentifier,
            new IssuanceParams(Amount: 1000));
        var assetId = issuance.AssetId;

        // Poll until Alice's VTXO with the asset appears
        await PollUntilAssetVtxo(alice, assetId, TimeSpan.FromSeconds(30));

        // Derive a receive contract for Bob
        var bobContract = await bob.contractService.DeriveContract(bob.walletIdentifier,
            NextContractPurpose.Receive);
        var bobAddress = bobContract.GetArkAddress();

        var serverInfo = await alice.clientTransport.GetServerInfoAsync();

        // Alice sends all 1000 asset units to Bob using explicit coin selection
        // to ensure correct input ordering for the asset packet.
        var spendingService = new SpendingService(
            alice.vtxoStorage, alice.contracts, alice.walletProvider,
            aliceCoinService, alice.contractService, alice.clientTransport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(), alice.safetyService, aliceIntentStorage);

        var aliceCoins = await spendingService.GetAvailableCoins(alice.walletIdentifier);

        // Find the coin carrying the asset
        var assetCoin = aliceCoins.First(c =>
            c.Assets is { Count: > 0 } assets && assets.Any(a => a.AssetId == assetId));

        // Send all 1000 units to Bob with explicit inputs (single asset coin).
        // Using explicit inputs avoids coin selector ordering issues with asset packets.
        await spendingService.Spend(alice.walletIdentifier,
            [assetCoin],
        [
            new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, bobAddress)
            {
                Assets = [new ArkTxOutAsset(assetId, 1000)]
            }
        ]);

        // Poll until Bob receives the asset VTXO
        await PollUntilAssetVtxo(bob, assetId, TimeSpan.FromSeconds(30));

        // Verify Bob has 1000 units
        var bobBalance = await GetAssetBalance(bob.vtxoStorage, assetId);
        Assert.That(bobBalance, Is.EqualTo(1000UL), "Bob should have 1000 asset units");
    }

    [Test, Order(3)]
    public async Task CanBurnAsset()
    {
        var app = SharedArkInfrastructure.App;
        var walletDetails = await FundedWalletHelper.GetFundedWallet(app);

        var (assetManager, _, _) = CreateAssetServices(walletDetails);

        // Issue 1000 units
        var issuance = await assetManager.IssueAsync(walletDetails.walletIdentifier,
            new IssuanceParams(Amount: 1000));
        var assetId = issuance.AssetId;

        // Poll until asset VTXO appears
        await PollUntilAssetVtxo(walletDetails, assetId, TimeSpan.FromSeconds(30));

        // Burn 400 units
        var burnTxId = await assetManager.BurnAsync(walletDetails.walletIdentifier,
            new BurnParams(assetId, 400));
        Assert.That(burnTxId, Is.Not.Null.And.Not.Empty, "Burn tx should return a valid txid");

        // Poll until the remaining asset balance reaches 600
        await PollUntilAssetBalance(walletDetails, assetId, 600, TimeSpan.FromSeconds(30));

        // Verify remaining balance
        var balance = await GetAssetBalance(walletDetails.vtxoStorage, assetId);
        Assert.That(balance, Is.EqualTo(600UL), "Remaining balance should be 600 after burning 400");
    }

    [Test, Order(4)]
    [Explicit("Assets do not yet survive batch settlement in arkd v0.9.0-rc.1 — " +
              "the batch round produces new VTXOs without asset annotations")]
    public async Task AssetsSurviveBatchSettlement()
    {
        var app = SharedArkInfrastructure.App;
        var walletDetails = await FundedWalletHelper.GetFundedWallet(app);

        var (assetManager, coinService, _) = CreateAssetServices(walletDetails);

        // Issue 1000 units
        var issuance = await assetManager.IssueAsync(walletDetails.walletIdentifier,
            new IssuanceParams(Amount: 1000));
        var assetId = issuance.AssetId;

        // Poll until asset VTXO arrives
        await PollUntilAssetVtxo(walletDetails, assetId, TimeSpan.FromSeconds(30));

        // Verify the asset exists before batch
        var preBatchBalance = await GetAssetBalance(walletDetails.vtxoStorage, assetId);
        Assert.That(preBatchBalance, Is.EqualTo(1000UL), "Pre-batch asset balance should be 1000");

        // Set up batch round services (same pattern as BatchSessionTests)
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, app.GetEndpoint("nbxplorer", "http"));
        var intentStorage = new InMemoryIntentStorage();

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(walletDetails.clientTransport, chainTimeProvider),
            walletDetails.clientTransport, walletDetails.contractService, chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            {
                Threshold = TimeSpan.FromHours(2),
                ThresholdHeight = 2000
            }));

        var newSuccessBatch = new TaskCompletionSource();
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                newSuccessBatch.TrySetResult();
        };

        var intentGenerationOptions = new OptionsWrapper<IntentGenerationServiceOptions>(
            new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromHours(5) });

        await using var intentGeneration = new IntentGenerationService(
            walletDetails.clientTransport,
            new DefaultFeeEstimator(walletDetails.clientTransport, chainTimeProvider),
            coinService, walletDetails.walletProvider, intentStorage,
            walletDetails.safetyService, walletDetails.contracts, walletDetails.vtxoStorage,
            scheduler, intentGenerationOptions);
        await intentGeneration.StartAsync(CancellationToken.None);

        await using var intentSync = new IntentSynchronizationService(
            intentStorage, walletDetails.clientTransport, walletDetails.safetyService);
        await intentSync.StartAsync(CancellationToken.None);

        await using var batchManager = new BatchManagementService(
            intentStorage, walletDetails.clientTransport, walletDetails.vtxoStorage,
            walletDetails.contracts, walletDetails.walletProvider, coinService,
            walletDetails.safetyService,
            Array.Empty<IEventHandler<PostBatchSessionEvent>>());
        await batchManager.StartAsync(CancellationToken.None);

        // Wait for the batch round to succeed
        await newSuccessBatch.Task.WaitAsync(TimeSpan.FromMinutes(2));

        // Give vtxo sync a moment to pick up post-batch VTXOs
        await Task.Delay(2000);
        await PollAllScripts(walletDetails);

        // Verify assets survived the batch
        var postBatchBalance = await GetAssetBalance(walletDetails.vtxoStorage, assetId);
        Assert.That(postBatchBalance, Is.EqualTo(1000UL),
            "Asset balance should be preserved after batch settlement");
    }

    [Test, Order(5)]
    [Explicit("Controlled issuance + reissuance requires IssueAsync to support creating both " +
              "control and main asset in one transaction (Go SDK NewControlAsset pattern). " +
              "The current SDK issues them separately, and AssetRef.FromId is not yet " +
              "compatible with arkd v0.9.0-rc.1 for controlled initial issuance.")]
    public async Task CanIssueControlledAssetAndReissue()
    {
        var app = SharedArkInfrastructure.App;
        var walletDetails = await FundedWalletHelper.GetFundedWallet(app);

        var (assetManager, _, _) = CreateAssetServices(walletDetails);

        // Step 1: Issue control asset + main asset in a single issuance.
        // The Go SDK supports NewControlAsset which issues both at once. Our SDK issues
        // them separately. Issue the control asset first (amount=1).
        var controlResult = await assetManager.IssueAsync(walletDetails.walletIdentifier,
            new IssuanceParams(Amount: 1));
        var controlAssetId = controlResult.AssetId;
        Assert.That(controlAssetId, Is.Not.Null.And.Not.Empty, "Control AssetId should be non-empty");

        // Poll until control VTXO appears
        await PollUntilAssetVtxo(walletDetails, controlAssetId, TimeSpan.FromSeconds(30));

        // Verify control asset exists
        var controlDetails = await walletDetails.clientTransport.GetAssetDetailsAsync(controlAssetId);
        Assert.That(controlDetails.Supply, Is.EqualTo(1UL), "Control asset supply should be 1");

        // Step 2: Reissue new supply using the control asset as authorization.
        // The reissuance builds a packet containing:
        // - A passthrough group that transfers the control asset from input to output (proves ownership)
        // - A reissuance group referencing the control asset ID with new outputs (adds supply)
        var reissueTxId = await assetManager.ReissueAsync(walletDetails.walletIdentifier,
            new ReissuanceParams(controlAssetId, 500));
        Assert.That(reissueTxId, Is.Not.Null.And.Not.Empty, "Reissuance tx should return a valid txid");

        // Poll until the newly reissued asset VTXO appears
        await Task.Delay(1000);
        await PollAllScripts(walletDetails);

        // Step 3: Verify control asset still has supply=1 after passthrough
        var controlDetailsAfter = await walletDetails.clientTransport.GetAssetDetailsAsync(controlAssetId);
        Assert.That(controlDetailsAfter.Supply, Is.EqualTo(1UL),
            "Control asset supply should remain 1 after reissuance");

        // Step 4: Reissue again to prove repeated reissuance works.
        // Re-poll to pick up the control asset's new VTXO from the passthrough.
        await PollUntilAssetVtxo(walletDetails, controlAssetId, TimeSpan.FromSeconds(30));

        var reissueTxId2 = await assetManager.ReissueAsync(walletDetails.walletIdentifier,
            new ReissuanceParams(controlAssetId, 300));
        Assert.That(reissueTxId2, Is.Not.Null.And.Not.Empty, "Second reissuance tx should return a valid txid");

        await Task.Delay(1000);
        await PollAllScripts(walletDetails);

        // Control asset still supply=1
        var controlDetailsFinal = await walletDetails.clientTransport.GetAssetDetailsAsync(controlAssetId);
        Assert.That(controlDetailsFinal.Supply, Is.EqualTo(1UL),
            "Control asset supply should remain 1 after second reissuance");
    }

    [Test, Order(6)]
    [Explicit("GetAssetDetailsAsync returns InvalidProtocolBufferException when the asset has metadata — " +
              "the client proto for AssetMetadata is incompatible with arkd v0.9.0-rc.1 response encoding")]
    public async Task CanIssueAssetWithMetadata()
    {
        var app = SharedArkInfrastructure.App;
        var walletDetails = await FundedWalletHelper.GetFundedWallet(app);

        var (assetManager, _, _) = CreateAssetServices(walletDetails);

        // Issue with metadata
        var metadata = new Dictionary<string, string>
        {
            { "name", "Test" },
            { "ticker", "TST" },
            { "decimals", "8" }
        };

        var result = await assetManager.IssueAsync(walletDetails.walletIdentifier,
            new IssuanceParams(Amount: 1000, Metadata: metadata));

        Assert.That(result.AssetId, Is.Not.Null.And.Not.Empty);

        // Poll until VTXO with asset appears
        await PollUntilAssetVtxo(walletDetails, result.AssetId, TimeSpan.FromSeconds(30));

        // Verify the asset VTXO carries 1000 units (from local storage)
        var balance = await GetAssetBalance(walletDetails.vtxoStorage, result.AssetId);
        Assert.That(balance, Is.EqualTo(1000UL), "Asset balance should be 1000 units");

        // Query GetAssetDetailsAsync and verify metadata
        var details = await walletDetails.clientTransport.GetAssetDetailsAsync(result.AssetId);
        Assert.That(details.AssetId, Is.EqualTo(result.AssetId));
        Assert.That(details.Supply, Is.EqualTo(1000UL));
        Assert.That(details.Metadata, Is.Not.Null, "Metadata should not be null");
        Assert.That(details.Metadata!["name"], Is.EqualTo("Test"));
        Assert.That(details.Metadata["ticker"], Is.EqualTo("TST"));
        Assert.That(details.Metadata["decimals"], Is.EqualTo("8"));
    }

    // --- Helper methods ---

    private static (AssetManager assetManager, CoinService coinService, InMemoryIntentStorage intentStorage)
        CreateAssetServices(
            (AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
                string walletIdentifier, InMemoryVtxoStorage vtxoStorage,
                ContractService contractService, InMemoryContractStorage contracts,
                IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) walletDetails)
    {
        var coinService = new CoinService(walletDetails.clientTransport, walletDetails.contracts,
        [
            new PaymentContractTransformer(walletDetails.walletProvider),
            new HashLockedContractTransformer(walletDetails.walletProvider)
        ]);

        var intentStorage = new InMemoryIntentStorage();

        var assetManager = new AssetManager(
            walletDetails.vtxoStorage,
            walletDetails.contracts,
            coinService,
            walletDetails.walletProvider,
            walletDetails.contractService,
            walletDetails.clientTransport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(),
            walletDetails.safetyService,
            intentStorage,
            []);

        return (assetManager, coinService, intentStorage);
    }

    /// <summary>
    /// Polls all active contract scripts to pick up new VTXOs.
    /// Needed because there is no PostSpendVtxoPollingHandler in manual wiring.
    /// </summary>
    private static async Task PollAllScripts(
        (AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier, InMemoryVtxoStorage vtxoStorage,
            ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) walletDetails)
    {
        await Task.Delay(500);
        var allContracts = await walletDetails.contracts.GetContracts(
            walletIds: [walletDetails.walletIdentifier]);
        foreach (var contract in allContracts)
        {
            await walletDetails.vtxoSync.PollScriptsForVtxos(
                new HashSet<string> { contract.Script });
        }
    }

    /// <summary>
    /// Polls all contract scripts repeatedly until an asset VTXO is found or timeout.
    /// </summary>
    private static async Task PollUntilAssetVtxo(
        (AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier, InMemoryVtxoStorage vtxoStorage,
            ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) walletDetails,
        string assetId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // Check if already present
            var vtxos = await walletDetails.vtxoStorage.GetVtxos(includeSpent: false);
            if (vtxos.Any(v => v.Assets is { Count: > 0 } assets &&
                               assets.Any(a => a.AssetId == assetId)))
                return;

            // Poll each script individually (batching can miss some VTXOs)
            var allContracts = await walletDetails.contracts.GetContracts(
                walletIds: [walletDetails.walletIdentifier]);
            foreach (var contract in allContracts)
            {
                await walletDetails.vtxoSync.PollScriptsForVtxos(
                    new HashSet<string> { contract.Script });
            }

            await Task.Delay(1000);
        }

        // Dump diagnostic info about VTXOs and scripts
        var finalVtxos = await walletDetails.vtxoStorage.GetVtxos(includeSpent: false);
        var vtxoInfo = string.Join("; ", finalVtxos.Select(v =>
            $"txid={v.TransactionId}:{v.TransactionOutputIndex} amount={v.Amount} script={v.Script[..20]}... " +
            $"assets=[{(v.Assets != null ? string.Join(",", v.Assets.Select(a => $"{a.AssetId}:{a.Amount}")) : "none")}]"));
        var diagContracts = await walletDetails.contracts.GetContracts(
            walletIds: [walletDetails.walletIdentifier]);
        var contractInfo = string.Join("; ", diagContracts.Select(c =>
            $"script={c.Script[..20]}... state={c.ActivityState}"));
        throw new TimeoutException(
            $"Timed out waiting for asset VTXO with assetId={assetId}. " +
            $"VTXOs in storage: [{vtxoInfo}]. " +
            $"Contracts: [{contractInfo}]");
    }

    /// <summary>
    /// Polls all contract scripts repeatedly until the asset balance matches expected value or timeout.
    /// </summary>
    private static async Task PollUntilAssetBalance(
        (AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier, InMemoryVtxoStorage vtxoStorage,
            ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) walletDetails,
        string assetId, ulong expectedBalance, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // Check current balance
            var balance = await GetAssetBalance(walletDetails.vtxoStorage, assetId);
            if (balance == expectedBalance)
                return;

            // Poll each script individually (batching can miss some VTXOs)
            var allContracts = await walletDetails.contracts.GetContracts(
                walletIds: [walletDetails.walletIdentifier]);
            foreach (var contract in allContracts)
            {
                await walletDetails.vtxoSync.PollScriptsForVtxos(
                    new HashSet<string> { contract.Script });
            }

            await Task.Delay(1000);
        }

        var finalBalance = await GetAssetBalance(walletDetails.vtxoStorage, assetId);
        throw new TimeoutException(
            $"Timed out waiting for asset balance. Expected={expectedBalance}, Actual={finalBalance}, AssetId={assetId}");
    }

    private static async Task WaitForAssetVtxo(InMemoryVtxoStorage vtxoStorage, string assetId,
        TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource();

        void Handler(object? sender, ArkVtxo vtxo)
        {
            if (!vtxo.IsSpent() && vtxo.Assets is { Count: > 0 } assets &&
                assets.Any(a => a.AssetId == assetId))
            {
                tcs.TrySetResult();
            }
        }

        vtxoStorage.VtxosChanged += Handler;

        try
        {
            // Check if already present
            var existing = await vtxoStorage.GetVtxos(includeSpent: false);
            if (existing.Any(v => v.Assets is { Count: > 0 } assets &&
                                  assets.Any(a => a.AssetId == assetId)))
            {
                return;
            }

            await tcs.Task.WaitAsync(timeout);
        }
        finally
        {
            vtxoStorage.VtxosChanged -= Handler;
        }
    }

    private static async Task WaitForAssetVtxoBalance(InMemoryVtxoStorage vtxoStorage, string assetId,
        ulong expectedBalance, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource();

        async void Handler(object? sender, ArkVtxo vtxo)
        {
            var balance = await GetAssetBalance(vtxoStorage, assetId);
            if (balance == expectedBalance)
                tcs.TrySetResult();
        }

        vtxoStorage.VtxosChanged += Handler;

        try
        {
            // Check if already at expected balance
            var currentBalance = await GetAssetBalance(vtxoStorage, assetId);
            if (currentBalance == expectedBalance)
                return;

            await tcs.Task.WaitAsync(timeout);
        }
        finally
        {
            vtxoStorage.VtxosChanged -= Handler;
        }
    }

    private static async Task<ulong> GetAssetBalance(InMemoryVtxoStorage vtxoStorage, string assetId)
    {
        var vtxos = await vtxoStorage.GetVtxos(includeSpent: false);
        return vtxos
            .Where(v => v.Assets is { Count: > 0 })
            .SelectMany(v => v.Assets!)
            .Where(a => a.AssetId == assetId)
            .Aggregate(0UL, (sum, a) => sum + a.Amount);
    }
}
