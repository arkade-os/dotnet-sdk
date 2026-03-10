using CliWrap;
using CliWrap.Buffered;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Wallet;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Services;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transport;
using NArk.Transport.GrpcClient;
using NBitcoin;

namespace NArk.Tests.End2End.Common;

internal static class FundedWalletHelper
{
    internal static async Task<(AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier,
            InMemoryVtxoStorage vtxoStorage, ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync)>
        GetFundedWallet()
    {
        var receivedFirstVtxoTcs = new TaskCompletionSource();
        var vtxoStorage = new InMemoryVtxoStorage();
        vtxoStorage.VtxosChanged += (sender, args) => receivedFirstVtxoTcs.TrySetResult();
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());

        var info = await clientTransport.GetServerInfoAsync();

        // Create a new wallet
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var contracts = new InMemoryContractStorage();
        var safetyService = new AsyncSafetyService();
        var fp1 = await walletProvider.CreateTestWallet();

        // Start vtxo synchronization service
        var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage,
            clientTransport,
            [vtxoStorage, contracts]
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contractService = new ContractService(walletProvider, contracts, clientTransport);

        // Generate a new payment contract, save to storage
        var signer = await (await walletProvider.GetAddressProviderAsync(fp1))!.GetNextSigningDescriptor();
        var contract = new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            signer
        );
        await contractService.ImportContract(fp1, contract);

        // Pay a random amount to the contract address
        const int randomAmount = 500000;
        var sendResult = await Cli.Wrap("docker")
            .WithArguments([
                "exec", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                randomAmount.ToString(), "--password", "secret"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (!sendResult.IsSuccess)
            throw new InvalidOperationException(
                $"ark send failed (exit={sendResult.ExitCode}): stdout={sendResult.StandardOutput}, stderr={sendResult.StandardError}");

        await receivedFirstVtxoTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        return (safetyService, walletProvider, fp1, vtxoStorage, contractService, contracts, clientTransport,
            vtxoSync);
    }

    /// <summary>
    /// Creates a funded wallet that derives <see cref="ArkDelegateContract"/> instead of
    /// <see cref="ArkPaymentContract"/> for Receive/SendToSelf purposes.
    /// Returns the same tuple as <see cref="GetFundedWallet"/> plus the initial delegate contract.
    /// </summary>
    internal static async Task<(AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier,
            InMemoryVtxoStorage vtxoStorage, ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync,
            ArkDelegateContract delegateContract)>
        GetFundedDelegateWallet(Uri delegatorEndpoint)
    {
        var receivedFirstVtxoTcs = new TaskCompletionSource();
        var vtxoStorage = new InMemoryVtxoStorage();
        vtxoStorage.VtxosChanged += (sender, args) => receivedFirstVtxoTcs.TrySetResult();
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());

        var info = await clientTransport.GetServerInfoAsync();

        // Get delegator pubkey
        var delegatorProvider = new GrpcDelegatorProvider(delegatorEndpoint.ToString());
        var delegatorInfo = await delegatorProvider.GetDelegatorInfoAsync();
        var delegateKey = KeyExtensions.ParseOutputDescriptor(delegatorInfo.Pubkey, info.Network);

        // Create wallet with DelegatingAddressProvider
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var contracts = new InMemoryContractStorage();
        var safetyService = new AsyncSafetyService();
        var walletId = await walletProvider.CreateTestWallet();

        // Wrap the address provider so it produces delegate contracts
        var innerAddressProvider = (await walletProvider.GetAddressProviderAsync(walletId))!;
        var delegatingProvider = new DelegatingAddressProvider(
            innerAddressProvider, delegateKey, info.SignerKey, info.UnilateralExit);
        walletProvider.SetAddressProvider(walletId, delegatingProvider);

        // Start vtxo synchronization service
        var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage,
            clientTransport,
            [vtxoStorage, contracts]
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contractService = new ContractService(walletProvider, contracts, clientTransport);

        // Derive a delegate contract (DelegatingAddressProvider converts Payment → Delegate)
        var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive);
        var delegateContract = (ArkDelegateContract)contract;

        // Fund via ark send
        const int randomAmount = 500000;
        var sendResult = await Cli.Wrap("docker")
            .WithArguments([
                "exec", "ark", "ark", "send", "--to", delegateContract.GetArkAddress().ToString(false), "--amount",
                randomAmount.ToString(), "--password", "secret"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (!sendResult.IsSuccess)
            throw new InvalidOperationException(
                $"ark send failed (exit={sendResult.ExitCode}): stdout={sendResult.StandardOutput}, stderr={sendResult.StandardError}");

        await receivedFirstVtxoTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        return (safetyService, walletProvider, walletId, vtxoStorage, contractService, contracts, clientTransport,
            vtxoSync, delegateContract);
    }
}
