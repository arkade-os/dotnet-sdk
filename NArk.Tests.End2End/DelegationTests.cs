using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Events;
using NArk.Blockchain;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Tests.Common;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;
using NBitcoin;

namespace NArk.Tests.End2End.Delegation;

public class DelegationTests
{
    [Test]
    public async Task CanGetDelegatorInfoViaRest()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync(
            $"{SharedDelegationInfrastructure.DelegatorEndpoint}/v1/delegator/info");

        Assert.That(response.IsSuccessStatusCode, Is.True,
            $"Delegator info endpoint returned {response.StatusCode}");

        var json = await response.Content.ReadFromJsonAsync<DelegatorInfoResponse>(
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        Assert.That(json?.Pubkey, Is.Not.Null.And.Not.Empty,
            "Delegator should return a non-empty public key");

        TestContext.Progress.WriteLine($"Delegator pubkey: {json!.Pubkey}");
        TestContext.Progress.WriteLine($"Delegator fee: {json.Fee}");
        TestContext.Progress.WriteLine($"Delegator address: {json.DelegatorAddress}");
    }

    [Test]
    public async Task CanGetDelegatorInfoViaGrpc()
    {
        var delegatorProvider = new GrpcDelegatorProvider(
            SharedDelegationInfrastructure.DelegatorEndpoint.ToString());

        var info = await delegatorProvider.GetDelegatorInfoAsync();

        Assert.That(info.Pubkey, Is.Not.Null.And.Not.Empty,
            "Delegator should return a non-empty public key via gRPC");

        TestContext.Progress.WriteLine($"Delegator pubkey (gRPC): {info.Pubkey}");
        TestContext.Progress.WriteLine($"Delegator fee (gRPC): {info.Fee}");
    }

    [Test]
    public async Task CanCreateDelegateContractWithDelegatorPubkey()
    {
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var serverInfo = await clientTransport.GetServerInfoAsync();

        // 1. Get delegator pubkey
        var delegatorProvider = new GrpcDelegatorProvider(
            SharedDelegationInfrastructure.DelegatorEndpoint.ToString());
        var delegatorInfo = await delegatorProvider.GetDelegatorInfoAsync();

        TestContext.Progress.WriteLine($"Delegator pubkey: {delegatorInfo.Pubkey}");

        // 2. Create wallet and derive delegate contract
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();

        var signer = await (await walletProvider.GetAddressProviderAsync(walletId))!
            .GetNextSigningDescriptor();
        var delegateKey = KeyExtensions.ParseOutputDescriptor(delegatorInfo.Pubkey, serverInfo.Network);

        var delegateContract = new ArkDelegateContract(
            serverInfo.SignerKey,
            serverInfo.UnilateralExit,
            signer,
            delegateKey);

        var arkAddress = delegateContract.GetArkAddress().ToString(false);
        TestContext.Progress.WriteLine($"Delegate contract address: {arkAddress}");

        // 3. Verify the contract has the expected structure
        var tapLeaves = delegateContract.GetTapScriptList();
        Assert.That(tapLeaves.Length, Is.EqualTo(3),
            "Delegate contract should have 3 tap leaves (forfeit, exit, delegate)");

        // 4. Verify round-trip parse via entity serialization
        var entity = delegateContract.ToEntity("test-wallet");
        var parsed = ArkDelegateContract.Parse(entity.AdditionalData, serverInfo.Network);
        Assert.That(parsed.GetArkAddress().ToString(false), Is.EqualTo(arkAddress),
            "Parsed contract should produce the same address");

        TestContext.Progress.WriteLine("Delegate contract creation + parse round-trip verified");
    }

    [Test]
    public async Task CanIssueAssetToDelegateContract()
    {
        var wallet = await FundedWalletHelper.GetFundedDelegateWallet(
            SharedDelegationInfrastructure.DelegatorEndpoint);

        // Wallet tuple without the delegateContract (matches AssetTestHelpers signature)
        var walletDetails = (wallet.safetyService, wallet.walletProvider,
            wallet.walletIdentifier, wallet.vtxoStorage, wallet.contractService,
            wallet.contracts, wallet.clientTransport, wallet.vtxoSync);

        var (assetManager, _, _) = AssetTestHelpers.CreateAssetServices(walletDetails,
            [new DelegateContractTransformer(wallet.walletProvider)]);

        // Issue 1000 units — asset VTXO should land at the delegate contract
        var result = await assetManager.IssueAsync(wallet.walletIdentifier,
            new IssuanceParams(Amount: 1000));

        Assert.That(result.AssetId, Is.Not.Null.And.Not.Empty, "AssetId should be non-empty");
        TestContext.Progress.WriteLine($"Issued asset {result.AssetId} to delegate contract");

        // Poll until the asset VTXO appears
        await AssetTestHelpers.PollUntilAssetVtxo(walletDetails, result.AssetId, TimeSpan.FromSeconds(30));

        // Verify balance
        var balance = await AssetTestHelpers.GetAssetBalance(wallet.vtxoStorage, result.AssetId);
        Assert.That(balance, Is.EqualTo(1000UL), "Should have 1000 asset units at delegate contract");

        // Verify the VTXO is at a delegate contract (not a payment contract)
        var vtxos = await wallet.vtxoStorage.GetVtxos(includeSpent: false);
        var assetVtxo = vtxos.First(v => v.Assets is { Count: > 0 } a &&
                                         a.Any(x => x.AssetId == result.AssetId));
        var contracts = await wallet.contracts.GetContracts(scripts: [assetVtxo.Script]);
        var entity = contracts.First();
        Assert.That(entity.Type, Is.EqualTo("Delegate").IgnoreCase,
            "Asset VTXO should be at a delegate contract");

        TestContext.Progress.WriteLine("Asset issuance to delegate contract verified");
    }

    [Test]
    public async Task DelegateAssetVtxoSurvivesBatchSettlement()
    {
        var wallet = await FundedWalletHelper.GetFundedDelegateWallet(
            SharedDelegationInfrastructure.DelegatorEndpoint);

        var walletDetails = (wallet.safetyService, wallet.walletProvider,
            wallet.walletIdentifier, wallet.vtxoStorage, wallet.contractService,
            wallet.contracts, wallet.clientTransport, wallet.vtxoSync);

        var delegateTransformer = new DelegateContractTransformer(wallet.walletProvider);
        var (assetManager, coinService, _) = AssetTestHelpers.CreateAssetServices(walletDetails,
            [delegateTransformer]);

        // Issue 1000 units
        var issuance = await assetManager.IssueAsync(wallet.walletIdentifier,
            new IssuanceParams(Amount: 1000));
        var assetId = issuance.AssetId;

        await AssetTestHelpers.PollUntilAssetVtxo(walletDetails, assetId, TimeSpan.FromSeconds(30));
        await AssetTestHelpers.PollAllScripts(walletDetails);

        var preBatchBalance = await AssetTestHelpers.GetAssetBalance(wallet.vtxoStorage, assetId);
        Assert.That(preBatchBalance, Is.EqualTo(1000UL), "Pre-batch asset balance should be 1000");

        // Set up batch services
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var intentStorage = TestStorage.CreateIntentStorage();

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(wallet.clientTransport, chainTimeProvider),
            wallet.clientTransport, wallet.contractService, chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            {
                Threshold = TimeSpan.FromHours(2),
                ThresholdHeight = 2000
            }));

        var newIntentTcs = new TaskCompletionSource();
        var newSubmittedIntentTcs = new TaskCompletionSource();
        var newSuccessBatch = new TaskCompletionSource();
        var batchFailedTcs = new TaskCompletionSource<string>();
        intentStorage.IntentChanged += (_, intent) =>
        {
            switch (intent.State)
            {
                case ArkIntentState.WaitingToSubmit:
                    newIntentTcs.TrySetResult();
                    break;
                case ArkIntentState.WaitingForBatch:
                    newSubmittedIntentTcs.TrySetResult();
                    break;
                case ArkIntentState.BatchSucceeded:
                    newSuccessBatch.TrySetResult();
                    break;
                case ArkIntentState.BatchFailed:
                    batchFailedTcs.TrySetResult(intent.CancellationReason ?? "unknown");
                    break;
            }
        };

        var intentGenerationOptions = new OptionsWrapper<IntentGenerationServiceOptions>(
            new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromHours(5) });

        // Step 1: Generate intent (includes asset packet OP_RETURN)
        await using var intentGeneration = new IntentGenerationService(
            wallet.clientTransport,
            new DefaultFeeEstimator(wallet.clientTransport, chainTimeProvider),
            coinService, wallet.walletProvider, intentStorage,
            wallet.safetyService, wallet.contracts, wallet.vtxoStorage,
            scheduler, intentGenerationOptions);
        await intentGeneration.StartAsync(CancellationToken.None);
        await newIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));

        // Step 2: Sync intent to arkd
        await using var intentSync = new IntentSynchronizationService(
            intentStorage, wallet.clientTransport, wallet.safetyService);
        await intentSync.StartAsync(CancellationToken.None);
        await newSubmittedIntentTcs.Task.WaitAsync(TimeSpan.FromMinutes(1));

        // Step 3: Participate in a batch
        await using var batchManager = new BatchManagementService(
            intentStorage, wallet.clientTransport, wallet.vtxoStorage,
            wallet.contracts, wallet.walletProvider, coinService,
            wallet.safetyService,
            Array.Empty<IEventHandler<PostBatchSessionEvent>>());
        await batchManager.StartAsync(CancellationToken.None);

        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(3));
        var completedTask = await Task.WhenAny(
            newSuccessBatch.Task,
            batchFailedTcs.Task,
            timeoutTask);

        if (completedTask == timeoutTask)
            Assert.Fail("Batch settlement timed out after 3 minutes");

        if (completedTask == batchFailedTcs.Task)
        {
            var reason = await batchFailedTcs.Task;
            Assert.Fail($"Batch failed: {reason}");
        }

        await newSuccessBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Give vtxo sync a moment to pick up post-batch VTXOs
        await Task.Delay(2000);
        await AssetTestHelpers.PollAllScripts(walletDetails);

        // Verify assets survived the batch
        var postBatchBalance = await AssetTestHelpers.GetAssetBalance(wallet.vtxoStorage, assetId);
        Assert.That(postBatchBalance, Is.EqualTo(1000UL),
            "Asset balance should be preserved after batch settlement at delegate contract");

        TestContext.Progress.WriteLine("Delegate asset VTXO survived batch settlement");
    }

    /// <summary>
    /// Minimal, plain-BTC (no assets) repeated-delegation-cycle test. Funds via a real
    /// arkd note redeemed through the full IntentGenerationService/IntentSynchronizationService/
    /// BatchManagementService pipeline, rather than through <see cref="ArkadeFaucet"/> like the
    /// rest of the suite: driving the note through our own batch machinery is part of what this
    /// test covers, so the faucet's plain offchain send would skip the code under test.
    /// Uses the production AddArkDelegation() builder wiring (DelegatingWalletProvider +
    /// DelegationMonitorService as a real IHostedService) instead of manual service construction.
    /// </summary>
    [Test]
    public async Task DelegateVtxoRenewsAcrossRepeatedCyclesViaNoteFunding()
    {
        var delegatorUri = SharedDelegationInfrastructure.DelegatorEndpoint.ToString();

        using var arkHost = Host.CreateDefaultBuilder([])
            .AddArk()
            .OnCustomGrpcArk(SharedArkInfrastructure.ArkdEndpoint.ToString())
            .WithSafetyService<AsyncSafetyService>()
            .WithIntentScheduler<SimpleIntentScheduler>()
            .WithWalletProvider<InMemoryWalletProvider>()
            .ConfigureServices((_, s) =>
            {
                s.AddDbContextFactory<TestDbContext>(options =>
                    options.UseInMemoryDatabase($"Test_{Guid.NewGuid():N}"));
                s.AddArkEfCoreStorage<TestDbContext>();
                s.AddNBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
                // AFTER WithWalletProvider — decorates IWalletProvider and registers
                // DelegationMonitorService as a real IHostedService.
                s.AddArkDelegation(delegatorUri);
            })
            .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
            {
                o.Threshold = TimeSpan.FromHours(2);
                o.ThresholdHeight = 2000;
            }))
            .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(o => o.PollInterval = TimeSpan.FromSeconds(5)))
            .Build();

        await arkHost.StartAsync();
        try
        {
            var walletProvider = arkHost.Services.GetRequiredService<InMemoryWalletProvider>();
            var contractService = arkHost.Services.GetRequiredService<IContractService>();
            var vtxoStorage = arkHost.Services.GetRequiredService<IVtxoStorage>();
            var contractStorage = arkHost.Services.GetRequiredService<IContractStorage>();

            var walletId = await walletProvider.CreateTestWallet();

            var note = await DockerHelper.CreateArkNote(500_000);
            if (string.IsNullOrEmpty(note))
                throw new Exception("Note creation failed!");

            await contractService.ImportContract(walletId, ArkNoteContract.Parse(note));

            // Wait for the note's redemption to land as a VTXO on a delegate contract.
            // IWalletProvider is decorated (AddArkDelegation), so whatever "send to self"
            // destination the redemption picks should already be an ArkDelegateContract.
            ArkVtxo? currentVtxo = null;
            var fundedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (currentVtxo is null && DateTime.UtcNow < fundedDeadline)
            {
                var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId], includeSpent: false);
                foreach (var v in vtxos)
                {
                    var contracts = await contractStorage.GetContracts(scripts: [v.Script]);
                    if (contracts.FirstOrDefault()?.Type.Equals("Delegate", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        currentVtxo = v;
                        break;
                    }
                }
                if (currentVtxo is null)
                    await Task.Delay(2000);
            }
            Assert.That(currentVtxo, Is.Not.Null,
                "Note redemption did not land a VTXO on a delegate contract within 90s");
            TestContext.Progress.WriteLine(
                $"Initial delegate VTXO: {currentVtxo!.TransactionId}:{currentVtxo.TransactionOutputIndex}, amount={currentVtxo.Amount}");

            // Delegate renewals rotate to a fresh HD-derived script every cycle (matching
            // ts-sdk's WalletReceiveRotator, which deliberately allocates a new receive
            // descriptor on every vtxo_received event rather than reusing one address) — so
            // renewal must be detected by wallet + contract-type + outpoint change, not by
            // watching a single fixed script.
            var renewalCount = 0;
            for (var round = 1; round <= 2; round++)
            {
                var roundDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(75);
                ArkVtxo? renewed = null;
                while (DateTime.UtcNow < roundDeadline)
                {
                    var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId], includeSpent: false);
                    foreach (var v in vtxos)
                    {
                        if (v.TransactionId == currentVtxo.TransactionId &&
                            v.TransactionOutputIndex == currentVtxo.TransactionOutputIndex)
                            continue;
                        if (v.Preconfirmed)
                            continue;
                        var contracts = await contractStorage.GetContracts(scripts: [v.Script]);
                        if (contracts.FirstOrDefault()?.Type.Equals("Delegate", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            renewed = v;
                            break;
                        }
                    }
                    if (renewed is not null)
                        break;
                    await Task.Delay(2000);
                }

                Assert.That(renewed, Is.Not.Null,
                    $"Delegator did not renew the VTXO during round {round} within 75s " +
                    $"(last outpoint was {currentVtxo.TransactionId}:{currentVtxo.TransactionOutputIndex})");
                Assert.That(renewed!.Amount, Is.LessThanOrEqualTo(currentVtxo.Amount),
                    $"round {round}: renewed amount should not exceed the pre-renewal amount");

                TestContext.Progress.WriteLine(
                    $"Round {round}: renewed to {renewed.TransactionId}:{renewed.TransactionOutputIndex}, amount={renewed.Amount}");
                currentVtxo = renewed;
                renewalCount++;
            }

            Assert.That(renewalCount, Is.EqualTo(2),
                "Expected the delegator to auto-renew the VTXO across 2 consecutive rounds");
        }
        finally
        {
            await arkHost.StopAsync();
        }
    }

    [Test]
    public async Task DelegationMonitorAutoRenewsAssetVtxoAcrossMultipleBatchRounds()
    {
        var wallet = await FundedWalletHelper.GetFundedDelegateWallet(
            SharedDelegationInfrastructure.DelegatorEndpoint);

        var walletDetails = (wallet.safetyService, wallet.walletProvider,
            wallet.walletIdentifier, wallet.vtxoStorage, wallet.contractService,
            wallet.contracts, wallet.clientTransport, wallet.vtxoSync);

        var delegateTransformer = new DelegateContractTransformer(wallet.walletProvider);
        var (assetManager, coinService, intentStorage) =
            AssetTestHelpers.CreateAssetServices(walletDetails, [delegateTransformer]);

        var delegatorProvider = new GrpcDelegatorProvider(
            SharedDelegationInfrastructure.DelegatorEndpoint.ToString());
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var feeEstimator = new DefaultFeeEstimator(wallet.clientTransport, chainTimeProvider);

        // Don't subscribe the monitor until consolidation is in flight (below): VtxosChanged
        // fires on ANY row change, so if the monitor were listening while the bare-dust asset
        // VTXO is still Preconfirmed, its later preconfirmed→settled update would trigger the
        // monitor to delegate (and spend) it out from under the consolidation Spend() call.
        var issuance = await assetManager.IssueAsync(wallet.walletIdentifier,
            new IssuanceParams(Amount: 1000));
        var assetId = issuance.AssetId;

        await AssetTestHelpers.PollUntilAssetVtxo(walletDetails, assetId, TimeSpan.FromSeconds(30));

        using var monitor = new DelegationMonitorService(
            wallet.vtxoStorage,
            wallet.contracts,
            [new DelegateContractDelegationTransformer(wallet.walletProvider)],
            delegatorProvider,
            wallet.walletProvider,
            wallet.clientTransport,
            feeEstimator);

        // AssetManager mints the asset carrier at exactly serverInfo.Dust (330 sats here) with
        // no headroom. arkd requires every offchain output to be >= dust, so once delegation's
        // intent fee (offchainInputFee, ~1% here) is deducted, a bare-dust renewal output always
        // falls under that floor — AMOUNT_TOO_LOW is unavoidable for this VTXO as issued, which is
        // exactly what DelegationMonitorService's dust guard skips rather than sending. Consolidate
        // it with the wallet's plain BTC funding VTXO into one delegate-contract output so the
        // renewal has room to pay the fee and still clear dust.
        var vtxosBeforeConsolidation = await wallet.vtxoStorage.GetVtxos(includeSpent: false);
        var assetVtxo = vtxosBeforeConsolidation.First(v =>
            v.Assets is { Count: > 0 } a && a.Any(x => x.AssetId == assetId));
        var fundingVtxo = vtxosBeforeConsolidation.First(v => v.Assets is not { Count: > 0 });

        var assetCoin = await coinService.GetCoin(assetVtxo, wallet.walletIdentifier);
        var fundingCoin = await coinService.GetCoin(fundingVtxo, wallet.walletIdentifier);

        var consolidatedContract = await wallet.contractService.DeriveContract(
            wallet.walletIdentifier, NextContractPurpose.SendToSelf,
            [assetCoin.Contract, fundingCoin.Contract]);
        var consolidatedAddress = consolidatedContract.GetArkAddress();

        // SpendingService.Spend is a direct ark-tx send, not an intent registration — it
        // computes its own change (totalInput - outputsSum) and auto-adds a change output
        // for any leftover. Pre-subtracting a fee here (as if for RegisterIntent) would just
        // leave a gap that Spend fills with a *second* delegate-eligible VTXO. Consolidate the
        // full amount into one output instead.
        var consolidatedOutput = new ArkTxOut(
            ArkTxOutType.Vtxo, assetCoin.Amount + fundingCoin.Amount, consolidatedAddress)
        {
            Assets = [new ArkTxOutAsset(assetId, 1000)]
        };

        var spendingService = new SpendingService(
            wallet.vtxoStorage, wallet.contracts, wallet.walletProvider,
            coinService, wallet.contractService, wallet.clientTransport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(), wallet.safetyService, intentStorage);
        await spendingService.Spend(wallet.walletIdentifier, [assetCoin, fundingCoin], [consolidatedOutput]);

        // Subscribe now — the consolidation broadcast above is already in flight, and the poll
        // immediately below is what will discover + upsert the consolidated VTXO, firing
        // VtxosChanged for the first (and only) time while the monitor is listening.
        await monitor.StartAsync(CancellationToken.None);

        await AssetTestHelpers.PollUntilAssetVtxo(walletDetails, assetId, TimeSpan.FromSeconds(30));

        var lastOutpoint = (await GetAssetVtxo(wallet.vtxoStorage, assetId))?.OutPoint;
        Assert.That(lastOutpoint, Is.Not.Null, "Asset VTXO should exist after consolidation");
        TestContext.Progress.WriteLine($"Consolidated asset VTXO outpoint: {lastOutpoint}");

        var renewalCount = 0;
        for (var round = 1; round <= 2; round++)
        {
            var roundDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(75);
            OutPoint? renewedOutpoint = null;
            while (DateTime.UtcNow < roundDeadline)
            {
                await AssetTestHelpers.PollAllScripts(walletDetails);
                var current = await GetAssetVtxo(wallet.vtxoStorage, assetId);
                // A changed outpoint alone isn't proof of a delegator-driven batch renewal — a
                // direct ark-tx (e.g. SpendingService.Spend's own checkpoint-then-final-tx
                // settlement) can surface as two different observed txids for the *same* logical
                // spend, which would false-positive here. arkd's `is_preconfirmed` flag
                // (ArkVtxo.Preconfirmed) is the actual ground truth: it only flips to false once
                // the vtxo is settled via a finalized batch — exactly what auto-renewal is
                // supposed to produce. Require both: a new outpoint AND settled-not-preconfirmed.
                if (current is not null && current.OutPoint != lastOutpoint && !current.Preconfirmed)
                {
                    renewedOutpoint = current.OutPoint;
                    break;
                }

                await Task.Delay(3000);
            }

            Assert.That(renewedOutpoint, Is.Not.Null,
                $"Delegator did not renew the asset VTXO during batch {round} within 75s " +
                $"(last outpoint was {lastOutpoint})");

            var balance = await AssetTestHelpers.GetAssetBalance(wallet.vtxoStorage, assetId);
            Assert.That(balance, Is.EqualTo(1000UL),
                $"Asset balance should stay at 1000 after batch {round}");

            TestContext.Progress.WriteLine(
                $"Batch {round}: asset VTXO auto-renewed by delegator to outpoint {renewedOutpoint}");

            lastOutpoint = renewedOutpoint;
            renewalCount++;
        }

        Assert.That(renewalCount, Is.EqualTo(2),
            "Expected the delegator to auto-renew the asset VTXO across 2 consecutive batches");

        // Still parked at a delegate contract — not swept, not collapsed to a plain payment contract.
        var finalVtxos = await wallet.vtxoStorage.GetVtxos(includeSpent: false);
        var finalAssetVtxo = finalVtxos.First(v => v.Assets is { Count: > 0 } a &&
                                                    a.Any(x => x.AssetId == assetId));
        var finalContracts = await wallet.contracts.GetContracts(scripts: [finalAssetVtxo.Script]);
        Assert.That(finalContracts.First().Type, Is.EqualTo("Delegate").IgnoreCase,
            "Asset VTXO should still be at a delegate contract after multiple auto-renewals");

        TestContext.Progress.WriteLine(
            "Delegation monitor kept the asset VTXO alive across 2 consecutive batches without owner intervention");
    }

    private static async Task<ArkVtxo?> GetAssetVtxo(IVtxoStorage vtxoStorage, string assetId)
    {
        var vtxos = await vtxoStorage.GetVtxos(includeSpent: false);
        return vtxos.FirstOrDefault(v => v.Assets is { Count: > 0 } a && a.Any(x => x.AssetId == assetId));
    }

    private record DelegatorInfoResponse(
        [property: JsonPropertyName("pubkey")] string? Pubkey,
        [property: JsonPropertyName("fee")] string? Fee,
        [property: JsonPropertyName("delegatorAddress")] string? DelegatorAddress);
}
