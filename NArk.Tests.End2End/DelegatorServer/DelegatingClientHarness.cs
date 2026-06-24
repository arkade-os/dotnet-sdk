using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Safety.AsyncKeyedLock;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;

namespace NArk.Tests.End2End.DelegatorServer;

/// <summary>
/// A delegate-contract client wired to a delegator endpoint. It derives a delegate contract using the
/// delegator's pubkey, starts the <see cref="DelegationMonitorService"/> BEFORE funding, then funds —
/// so the first VTXO insert reliably triggers an automatic delegation to the delegator. Reused by the
/// spike and the intake / refresh e2e tests.
/// </summary>
internal sealed class DelegatingClientHarness : IAsyncDisposable
{
    public required InMemoryWalletProvider WalletProvider { get; init; }
    public required string WalletId { get; init; }
    public required ArkDelegateContract DelegateContract { get; init; }
    public required IVtxoStorage VtxoStorage { get; init; }
    public required IContractStorage ContractStorage { get; init; }
    public required IClientTransport ClientTransport { get; init; }
    public required VtxoSynchronizationService VtxoSync { get; init; }
    public required DelegationMonitorService Monitor { get; init; }

    /// <summary>
    /// Sets up the delegate-contract client against <paramref name="delegatorGrpcEndpoint"/>, starts the
    /// monitor, and funds the delegate contract. On return the delegation is in flight (the monitor fires
    /// once vtxoSync detects the funded VTXO).
    /// </summary>
    public static async Task<DelegatingClientHarness> CreateAndDelegateAsync(
        string delegatorGrpcEndpoint, long amountSats = 500_000)
    {
        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var info = await clientTransport.GetServerInfoAsync();

        var delegatorProvider = new GrpcDelegatorProvider(delegatorGrpcEndpoint);
        var delegatorInfo = await delegatorProvider.GetDelegatorInfoAsync();
        var delegateKey = KeyExtensions.ParseOutputDescriptor(delegatorInfo.Pubkey, info.Network);

        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();
        var inner = (await walletProvider.GetAddressProviderAsync(walletId))!;
        walletProvider.SetAddressProvider(walletId,
            new DelegatingAddressProvider(inner, delegateKey, info.SignerKey, info.UnilateralExit));

        var vtxoSync = new VtxoSynchronizationService(
            storage.VtxoStorage, clientTransport, [storage.VtxoStorage, storage.ContractStorage]);
        await vtxoSync.StartAsync(CancellationToken.None);

        var contractService = new ContractService(walletProvider, storage.ContractStorage, clientTransport);
        var delegateContract = (ArkDelegateContract)await contractService.DeriveContract(
            walletId, NextContractPurpose.Receive);

        // Start the monitor BEFORE funding so the first VTXO insert triggers the delegation.
        var monitor = new DelegationMonitorService(
            storage.VtxoStorage, storage.ContractStorage,
            [new DelegateContractDelegationTransformer(walletProvider)],
            delegatorProvider, walletProvider, clientTransport);
        await monitor.StartAsync(CancellationToken.None);

        await DockerHelper.SendArkdNoteTo(delegateContract.GetArkAddress().ToString(false), amountSats);

        return new DelegatingClientHarness
        {
            WalletProvider = walletProvider,
            WalletId = walletId,
            DelegateContract = delegateContract,
            VtxoStorage = storage.VtxoStorage,
            ContractStorage = storage.ContractStorage,
            ClientTransport = clientTransport,
            VtxoSync = vtxoSync,
            Monitor = monitor
        };
    }

    public async ValueTask DisposeAsync()
    {
        Monitor.Dispose();
        await VtxoSync.DisposeAsync();
    }
}
