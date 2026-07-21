using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.CoinSelector;
using NArk.Core.Contracts;
using NArk.Core.Events;
using NArk.Blockchain;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
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
            "Delegate contract should have 3 tap leaves (delegate, forfeit, exit)");

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

        // Set up batch round services
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

        // Step 3: Participate in batch round
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

        // Real automatic delegation: the wallet only watches its own VTXOs and hands off
        // pre-signed artifacts. It never runs IntentGenerationService/BatchManagementService
        // itself here — the live delegator is solely responsible for joining batch rounds
        // and refreshing the VTXO before it expires.
        var delegatorProvider = new GrpcDelegatorProvider(
            SharedDelegationInfrastructure.DelegatorEndpoint.ToString());
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var feeEstimator = new DefaultFeeEstimator(wallet.clientTransport, chainTimeProvider);
        using var monitor = new DelegationMonitorService(
            wallet.vtxoStorage,
            wallet.contracts,
            [new DelegateContractDelegationTransformer(wallet.walletProvider)],
            delegatorProvider,
            wallet.walletProvider,
            wallet.clientTransport,
            feeEstimator);
        await monitor.StartAsync(CancellationToken.None);

        // Issuing lands the asset VTXO on the delegate contract and fires VtxosChanged,
        // which the monitor picks up to auto-delegate it.
        var issuance = await assetManager.IssueAsync(wallet.walletIdentifier,
            new IssuanceParams(Amount: 1000));
        var assetId = issuance.AssetId;

        await AssetTestHelpers.PollUntilAssetVtxo(walletDetails, assetId, TimeSpan.FromSeconds(30));

        // AssetManager mints the asset carrier at exactly serverInfo.Dust (330 sats here) with
        // no headroom. arkd requires every offchain output to be >= dust, so once delegation's
        // intent fee (offchainInputFee, ~1% here) is deducted, a bare-dust renewal output always
        // falls under that floor — AMOUNT_TOO_LOW is unavoidable for this VTXO as issued. Consolidate
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

        var consolidationFee = await feeEstimator.EstimateFeeAsync(
            [assetCoin, fundingCoin],
            [new ArkTxOut(ArkTxOutType.Vtxo, assetCoin.Amount + fundingCoin.Amount, consolidatedAddress)]);
        var consolidatedOutput = new ArkTxOut(
            ArkTxOutType.Vtxo, assetCoin.Amount + fundingCoin.Amount - Money.Satoshis(consolidationFee),
            consolidatedAddress)
        {
            Assets = [new ArkTxOutAsset(assetId, 1000)]
        };

        var spendingService = new SpendingService(
            wallet.vtxoStorage, wallet.contracts, wallet.walletProvider,
            coinService, wallet.contractService, wallet.clientTransport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(), wallet.safetyService, intentStorage);
        await spendingService.Spend(wallet.walletIdentifier, [assetCoin, fundingCoin], [consolidatedOutput]);

        await AssetTestHelpers.PollUntilAssetVtxo(walletDetails, assetId, TimeSpan.FromSeconds(30));

        var lastOutpoint = await GetAssetVtxoOutpoint(wallet.vtxoStorage, assetId);
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
                var current = await GetAssetVtxoOutpoint(wallet.vtxoStorage, assetId);
                if (current is not null && current != lastOutpoint)
                {
                    renewedOutpoint = current;
                    break;
                }

                await Task.Delay(3000);
            }

            Assert.That(renewedOutpoint, Is.Not.Null,
                $"Delegator did not renew the asset VTXO during batch round {round} within 75s " +
                $"(last outpoint was {lastOutpoint})");

            var balance = await AssetTestHelpers.GetAssetBalance(wallet.vtxoStorage, assetId);
            Assert.That(balance, Is.EqualTo(1000UL),
                $"Asset balance should stay at 1000 after batch round {round}");

            TestContext.Progress.WriteLine(
                $"Batch round {round}: asset VTXO auto-renewed by delegator to outpoint {renewedOutpoint}");

            lastOutpoint = renewedOutpoint;
            renewalCount++;
        }

        Assert.That(renewalCount, Is.EqualTo(2),
            "Expected the delegator to auto-renew the asset VTXO across 2 consecutive batch rounds");

        // Still parked at a delegate contract — not swept, not collapsed to a plain payment contract.
        var finalVtxos = await wallet.vtxoStorage.GetVtxos(includeSpent: false);
        var finalAssetVtxo = finalVtxos.First(v => v.Assets is { Count: > 0 } a &&
                                                    a.Any(x => x.AssetId == assetId));
        var finalContracts = await wallet.contracts.GetContracts(scripts: [finalAssetVtxo.Script]);
        Assert.That(finalContracts.First().Type, Is.EqualTo("Delegate").IgnoreCase,
            "Asset VTXO should still be at a delegate contract after multiple auto-renewals");

        TestContext.Progress.WriteLine(
            "Delegation monitor kept the asset VTXO alive across 2 consecutive batch rounds without owner intervention");
    }

    private static async Task<OutPoint?> GetAssetVtxoOutpoint(IVtxoStorage vtxoStorage, string assetId)
    {
        var vtxos = await vtxoStorage.GetVtxos(includeSpent: false);
        var vtxo = vtxos.FirstOrDefault(v => v.Assets is { Count: > 0 } a && a.Any(x => x.AssetId == assetId));
        return vtxo is null ? null : new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);
    }

    private record DelegatorInfoResponse(
        [property: JsonPropertyName("pubkey")] string? Pubkey,
        [property: JsonPropertyName("fee")] string? Fee,
        [property: JsonPropertyName("delegatorAddress")] string? DelegatorAddress);
}
