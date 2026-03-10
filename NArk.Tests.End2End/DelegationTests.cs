using System.Net.Http.Json;
using CliWrap;
using CliWrap.Buffered;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;
using NBitcoin;

namespace NArk.Tests.End2End.Delegation;

public class DelegationTests
{
    [Test]
    public async Task CanGetDelegatePublicKey()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync(
            $"{SharedDelegationInfrastructure.DelegatorRestEndpoint}/v1/delegate/pubkey");

        Assert.That(response.IsSuccessStatusCode, Is.True,
            $"Delegator pubkey endpoint returned {response.StatusCode}");

        var json = await response.Content.ReadFromJsonAsync<DelegatePubkeyResponse>();
        Assert.That(json?.PublicKey, Is.Not.Null.And.Not.Empty,
            "Delegator should return a non-empty public key");

        TestContext.Progress.WriteLine($"Delegate pubkey: {json!.PublicKey}");
    }

    [Test]
    public async Task CanWatchAndUnwatchDelegateContract()
    {
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var serverInfo = await clientTransport.GetServerInfoAsync();

        // 1. Get delegate pubkey
        using var http = new HttpClient();
        var pubkeyResponse = await http.GetAsync(
            $"{SharedDelegationInfrastructure.DelegatorRestEndpoint}/v1/delegate/pubkey");
        var pubkeyJson = await pubkeyResponse.Content.ReadFromJsonAsync<DelegatePubkeyResponse>();
        var delegatePubkeyHex = pubkeyJson!.PublicKey!;

        TestContext.Progress.WriteLine($"Delegate pubkey: {delegatePubkeyHex}");

        // 2. Create wallet and delegate contract
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var contracts = new InMemoryContractStorage();
        var vtxoStorage = new InMemoryVtxoStorage();
        var walletId = await walletProvider.CreateTestWallet();

        var signer = await (await walletProvider.GetAddressProviderAsync(walletId))!
            .GetNextSigningDescriptor();
        var delegateKey = KeyExtensions.ParseOutputDescriptor(delegatePubkeyHex, serverInfo.Network);

        var delegateContract = new ArkDelegateContract(
            serverInfo.SignerKey,
            serverInfo.UnilateralExit,
            signer,
            delegateKey);

        var contractService = new ContractService(walletProvider, contracts, clientTransport);
        await contractService.ImportContract(walletId, delegateContract);

        var delegateAddress = delegateContract.GetArkAddress().ToString(false);
        TestContext.Progress.WriteLine($"Delegate contract address: {delegateAddress}");

        // 3. Start VTXO sync and fund the delegate contract via arkd
        var vtxoReceivedTcs = new TaskCompletionSource();
        vtxoStorage.VtxosChanged += (_, _) => vtxoReceivedTcs.TrySetResult();

        var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage, clientTransport, [vtxoStorage, contracts]);
        await vtxoSync.StartAsync(CancellationToken.None);

        var sendResult = await Cli.Wrap("docker")
            .WithArguments([
                "exec", "ark", "ark", "send",
                "--to", delegateAddress,
                "--amount", "100000",
                "--password", "secret"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        Assert.That(sendResult.IsSuccess, Is.True,
            $"ark send failed: {sendResult.StandardOutput} {sendResult.StandardError}");

        await vtxoReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var delegateVtxos = (await vtxoStorage.GetVtxos(
            scripts: [delegateContract.GetScriptPubKey().ToHex()]))
            .ToList();

        Assert.That(delegateVtxos, Is.Not.Empty, "Should have VTXOs at delegate contract address");
        TestContext.Progress.WriteLine($"Found {delegateVtxos.Count} delegate VTXOs");

        // 4. Create DelegationService and watch the address
        var delegatorProvider = new GrpcDelegatorProvider(
            SharedDelegationInfrastructure.DelegatorGrpcEndpoint.ToString());
        var delegationService = new DelegationService(
            [new DelegateContractDelegationTransformer(walletProvider)],
            delegatorProvider,
            clientTransport,
            contracts);

        var watchResult = await delegationService.WatchForRolloverAsync(
            walletId,
            delegateVtxos,
            destinationAddress: delegateAddress);

        Assert.That(watchResult.WatchedAddresses, Is.Not.Empty,
            "Should have watched at least one address");
        Assert.That(watchResult.FailedOutpoints, Is.Empty,
            "No outpoints should have failed");

        TestContext.Progress.WriteLine($"Watched: {string.Join(", ", watchResult.WatchedAddresses)}");

        // 5. Verify in ListWatched
        var watched = await delegationService.ListWatchedAsync();
        Assert.That(watched.Any(w => w.Address == watchResult.WatchedAddresses[0]),
            Is.True, "Watched address should appear in list");

        // 6. Unwatch and verify removal
        await delegationService.UnwatchAsync(watchResult.WatchedAddresses[0]);

        var afterUnwatch = await delegationService.ListWatchedAsync();
        Assert.That(afterUnwatch.Any(w => w.Address == watchResult.WatchedAddresses[0]),
            Is.False, "Unwatched address should no longer appear");

        TestContext.Progress.WriteLine("Watch/Unwatch delegation cycle completed");

        await vtxoSync.StopAsync(CancellationToken.None);
    }

    private record DelegatePubkeyResponse(string? PublicKey);
}
