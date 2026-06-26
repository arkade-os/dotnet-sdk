using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Wallet;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Services;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transport;
using NArk.Tests.Common;
using NArk.Transport.GrpcClient;

namespace NArk.Tests.End2End.Common;

internal static class FundedWalletHelper
{
    internal static async Task<(ISafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier,
            IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync)>
        GetFundedWallet(int vtxoCount = 1, int amountSatsPerVtxo = 500_000)
    {
        if (vtxoCount < 1) throw new ArgumentOutOfRangeException(nameof(vtxoCount), "must be >= 1");

        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);

        var receivedVtxoCount = 0;
        var receivedAllVtxosTcs = new TaskCompletionSource();
        storage.VtxoStorage.VtxosChanged += (sender, args) =>
        {
            if (Interlocked.Increment(ref receivedVtxoCount) >= vtxoCount)
                receivedAllVtxosTcs.TrySetResult();
        };
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());

        var info = await clientTransport.GetServerInfoAsync();

        // Create a new wallet
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var fp1 = await walletProvider.CreateTestWallet();

        // Start vtxo synchronization service
        var vtxoSync = new VtxoSynchronizationService(
            storage.VtxoStorage,
            clientTransport,
            [storage.VtxoStorage, storage.ContractStorage]
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contractService = new ContractService(walletProvider, storage.ContractStorage, clientTransport);

        // Generate a new payment contract, save to storage
        var signer = await (await walletProvider.GetAddressProviderAsync(fp1))!.GetNextSigningDescriptor();
        var contract = new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            signer
        );
        await contractService.ImportContract(fp1, contract);

        // Pay <vtxoCount> separate VTXOs to the contract address. Each
        // ark send produces an independent VTXO of <amountSatsPerVtxo> sats
        // so concurrent tests that need parallel coin selection don't race
        // on a shared input.
        for (var i = 0; i < vtxoCount; i++)
            await DockerHelper.SendArkdNoteTo(contract.GetArkAddress().ToString(false), amountSatsPerVtxo);

        await receivedAllVtxosTcs.Task.WaitAsync(TimeSpan.FromSeconds(15 * vtxoCount));

        return (safetyService, walletProvider, fp1, storage.VtxoStorage, contractService, storage.ContractStorage,
            clientTransport, vtxoSync);
    }

    /// <summary>
    /// Creates a funded wallet that derives <see cref="ArkDelegateContract"/> instead of
    /// <see cref="ArkPaymentContract"/> for Receive/SendToSelf purposes.
    /// Returns the same tuple as <see cref="GetFundedWallet"/> plus the initial delegate contract.
    /// </summary>
    internal static async Task<(ISafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier,
            IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync,
            ArkDelegateContract delegateContract)>
        GetFundedDelegateWallet(Uri delegatorEndpoint)
    {
        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);

        var receivedFirstVtxoTcs = new TaskCompletionSource();
        storage.VtxoStorage.VtxosChanged += (sender, args) => receivedFirstVtxoTcs.TrySetResult();
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());

        var info = await clientTransport.GetServerInfoAsync();

        // Get delegator pubkey
        var delegatorProvider = new GrpcDelegatorProvider(delegatorEndpoint.ToString());
        var delegatorInfo = await delegatorProvider.GetDelegatorInfoAsync();
        var delegateKey = KeyExtensions.ParseOutputDescriptor(delegatorInfo.Pubkey, info.Network);

        // Create wallet with DelegatingAddressProvider
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();

        // Wrap the address provider so it produces delegate contracts
        var innerAddressProvider = (await walletProvider.GetAddressProviderAsync(walletId))!;
        var delegatingProvider = new DelegatingAddressProvider(
            innerAddressProvider, delegateKey, info.SignerKey, info.UnilateralExit);
        walletProvider.SetAddressProvider(walletId, delegatingProvider);

        // Start vtxo synchronization service
        var vtxoSync = new VtxoSynchronizationService(
            storage.VtxoStorage,
            clientTransport,
            [storage.VtxoStorage, storage.ContractStorage]
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contractService = new ContractService(walletProvider, storage.ContractStorage, clientTransport);

        // Derive a delegate contract (DelegatingAddressProvider converts Payment → Delegate)
        var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive);
        var delegateContract = (ArkDelegateContract)contract;

        const int randomAmount = 500000;
        await DockerHelper.SendArkdNoteTo(delegateContract.GetArkAddress().ToString(false), randomAmount);

        await receivedFirstVtxoTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        return (safetyService, walletProvider, walletId, storage.VtxoStorage, contractService, storage.ContractStorage,
            clientTransport, vtxoSync, delegateContract);
    }
}
