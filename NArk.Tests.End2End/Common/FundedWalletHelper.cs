using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using NArk.Core.Contracts;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Services;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transport;
using NArk.Transport.GrpcClient;
namespace NArk.Tests.End2End.Common;

internal static class FundedWalletHelper
{
    internal static async Task<(AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier,
            InMemoryVtxoStorage vtxoStorage, ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync)>
        GetFundedWallet(DistributedApplication app)
    {
        var receivedFirstVtxoTcs = new TaskCompletionSource();
        var vtxoStorage = new InMemoryVtxoStorage();
        vtxoStorage.VtxosChanged += (sender, args) => receivedFirstVtxoTcs.TrySetResult();
        var clientTransport = new GrpcClientTransport(app.GetEndpoint("ark", "arkd").ToString());

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
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        await receivedFirstVtxoTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        return (safetyService, walletProvider, fp1, vtxoStorage, contractService, contracts, clientTransport,
            vtxoSync);
    }
}