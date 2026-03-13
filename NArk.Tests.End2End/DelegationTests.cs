using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Core.CoinSelector;
using NArk.Core.Contracts;
using NArk.Core.Events;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
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
        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
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

    private record DelegatorInfoResponse(
        [property: JsonPropertyName("pubkey")] string? Pubkey,
        [property: JsonPropertyName("fee")] string? Fee,
        [property: JsonPropertyName("delegatorAddress")] string? DelegatorAddress);
}
