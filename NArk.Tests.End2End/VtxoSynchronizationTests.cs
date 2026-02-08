using System.Security.Cryptography;
using CliWrap;
using CliWrap.Buffered;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Contracts;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Services;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;
using NArk.Transport.GrpcClient;
using NBitcoin;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End;

public class VtxoSynchronizationTests
{
    [Test]
    public async Task CanReceiveVtxosFromImportedContract()
    {
        var app = SharedArkInfrastructure.App;
        // Receive arkd information
        var clientTransport = new GrpcClientTransport(app.GetEndpoint("ark", "arkd").ToString());
        var info = await clientTransport.GetServerInfoAsync();

        // Pay a random amount to the contract address
        var randomAmount = RandomNumberGenerator.GetInt32((int)info.Dust.Satoshi, 100000);

        // Listen for incoming vtxos
        var receiveTcs = new TaskCompletionSource();
        var vtxoStorage = new InMemoryVtxoStorage();

        vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && vtxo.Amount == (ulong)randomAmount)
            {
                receiveTcs.TrySetResult();
            }
        };

        // Create a new wallet
        var contracts = new InMemoryContractStorage();
        var safetyService = new AsyncSafetyService();
        var wallet = new InMemoryWalletProvider(clientTransport);
        var fp = await wallet.CreateTestWallet();

        // Start vtxo synchronization service
        await using var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage,
            clientTransport,
            [vtxoStorage, contracts]
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contractService = new ContractService(wallet, contracts, clientTransport);

        // Generate a new payment contract, save to storage
        var signer = await (await wallet.GetAddressProviderAsync(fp))!.GetNextSigningDescriptor();
        var contract = new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            signer
        );
        await contractService.ImportContract(fp, contract);

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        // Wait for the sync service to receive it
        await receiveTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task AwaitingContractsAutoDeactivateWhenFundsReceived()
    {
        var app = SharedArkInfrastructure.App;
        // Receive arkd information
        var clientTransport = new GrpcClientTransport(app.GetEndpoint("ark", "arkd").ToString());
        var info = await clientTransport.GetServerInfoAsync();

        // Create wallet and storage
        var inMemoryWalletProvider = new InMemoryWalletProvider(clientTransport);
        var contracts = new InMemoryContractStorage();
        var vtxoStorage = new InMemoryVtxoStorage();

        var fp = await inMemoryWalletProvider.CreateTestWallet();
        var contractService = new ContractService(inMemoryWalletProvider, contracts, clientTransport);

        // Start vtxo synchronization service (handles auto-deactivation)
        await using var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage,
            clientTransport,
            [vtxoStorage, contracts]
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        // Derive a contract with AwaitingFundsBeforeDeactivate state (simulates SendToSelf with no static sweep)
        var contract = await contractService.DeriveContract(
            fp,
            NextContractPurpose.SendToSelf,
            ContractActivityState.AwaitingFundsBeforeDeactivate);
        var contractAddress = contract.GetArkAddress();
        var contractScript = contractAddress.ScriptPubKey.ToHex();

        // Verify contract is in AwaitingFundsBeforeDeactivate state
        var contractsBefore = await contracts.GetContracts(isActive: true);
        var awaitingContract = contractsBefore.FirstOrDefault(c => c.Script == contractScript);
        Assert.That(awaitingContract, Is.Not.Null, "Contract should exist");
        Assert.That(awaitingContract!.ActivityState, Is.EqualTo(ContractActivityState.AwaitingFundsBeforeDeactivate),
            "Contract should be in AwaitingFundsBeforeDeactivate state");

        // Wait for contract deactivation
        var deactivationTcs = new TaskCompletionSource();
        contracts.ContractsChanged += (_, changedContract) =>
        {
            if (changedContract.Script == contractScript &&
                changedContract.ActivityState == ContractActivityState.Inactive)
            {
                deactivationTcs.TrySetResult();
            }
        };

        // Send funds to the contract
        var randomAmount = RandomNumberGenerator.GetInt32((int)info.Dust.Satoshi, 100000);
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contractAddress.ToString(false),
                "--amount", randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        // Wait for auto-deactivation
        await deactivationTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Verify contract is now Inactive
        var allContracts = await contracts.GetContracts(walletIds: [fp]);
        var deactivatedContract = allContracts.FirstOrDefault(c => c.Script == contractScript);
        Assert.That(deactivatedContract, Is.Not.Null, "Contract should still exist");
        Assert.That(deactivatedContract!.ActivityState, Is.EqualTo(ContractActivityState.Inactive),
            "Contract should be deactivated after receiving funds");
    }

    [Test]
    public async Task CanSendAndReceiveBackVtxo()
    {
        var app = SharedArkInfrastructure.App;
        // Receive arkd information
        var clientTransport = new GrpcClientTransport(app.GetEndpoint("ark", "arkd").ToString());

        // Create a new wallet
        var inMemoryWalletProvider = new InMemoryWalletProvider(clientTransport);
        var contracts = new InMemoryContractStorage();

        var vtxoStorage = new InMemoryVtxoStorage();

        var safetyService = new AsyncSafetyService();

        var fp1 = await inMemoryWalletProvider.CreateTestWallet();
        var fp2 = await inMemoryWalletProvider.CreateTestWallet();

        var contractService = new ContractService(inMemoryWalletProvider, contracts, clientTransport);

        // Start vtxo synchronization service
        await using var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage,
            clientTransport,
            [vtxoStorage, contracts]
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contract = await contractService.DeriveContract(fp1, NextContractPurpose.Receive);
        var wallet1Address = contract.GetArkAddress();

        // Pay a random amount to the contract address
        var randomAmount = 50000;
        var receiveTcs = new TaskCompletionSource();
        var receiveHalfTcs = new TaskCompletionSource();

        vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && (ulong)randomAmount == vtxo.Amount)
            {
                receiveTcs.TrySetResult();
            }
            else if (!vtxo.IsSpent() && (ulong)(randomAmount / 2) == vtxo.Amount)
            {
                receiveHalfTcs.TrySetResult();
            }
        };

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", wallet1Address.ToString(false),
                "--amount", randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        // Wait for the sync service to receive it
        await receiveTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Generate a new payment contract to receive funds from first wallet, save to storage
        var contract2 = await contractService.DeriveContract(fp2, NextContractPurpose.Receive);
        var wallet2Address = contract2.GetArkAddress();

        var chainTimeProvider = new ChainTimeProvider(Network.RegTest, app.GetEndpoint("nbxplorer", "http"));

        var coinService = new CoinService(clientTransport, contracts,
            [new PaymentContractTransformer(inMemoryWalletProvider), new HashLockedContractTransformer(inMemoryWalletProvider)]);

        var spendingService = new SpendingService(vtxoStorage, contracts,
            inMemoryWalletProvider, coinService, contractService, clientTransport, new DefaultCoinSelector(), safetyService, new InMemoryIntentStorage());

        await spendingService.Spend(fp1,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(randomAmount / 2), wallet2Address)
        ]);

        await receiveHalfTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}