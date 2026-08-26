using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Extensions;
using NArk.Core.Services;
using NArk.Safety.AsyncKeyedLock;
using NArk.Tests.Common;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;

namespace NArk.Tests.End2End;

public class ArkadeCashTests
{
    [Test]
    public async Task RoundripsCorrectly()
    {
        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var contractService = new ContractService(walletProvider, storage.ContractStorage, clientTransport);

        await using var vtxoSync = new VtxoSynchronizationService(
            storage.VtxoStorage,
            clientTransport,
            [storage.VtxoStorage, storage.ContractStorage]);
        await vtxoSync.StartAsync(CancellationToken.None);
        
        var serverInfo = await clientTransport.GetServerInfoAsync();
        var cash = await CreateFundedArkadeCash(serverInfo, 100000);

        var receiverWalletId = await walletProvider.CreateTestWallet();
        await contractService.ImportContract(receiverWalletId, cash.ToContract(serverInfo));

        var cashScript = cash.GetAddress(serverInfo.Network).ScriptPubKey.ToHex();
        await ForcePollScript(vtxoSync, cashScript, TimeSpan.FromSeconds(15));

        var receiverUnspent = await storage.VtxoStorage.GetVtxos(
            walletIds: [receiverWalletId],
            includeSpent: false);
        var receiverAmount = receiverUnspent.Select(v => v.Amount).Aggregate(0UL, (acc, x) => acc + x);
        
        Assert.That(receiverUnspent.Count, Is.EqualTo(1),
            "Receiver should have at least one unspent ArkadeCash VTXO");
        Assert.That(receiverAmount, Is.EqualTo(100000UL),
            "Receiver unspent amount should be greater than zero");
    }

    private static async Task<ArkadeCash> CreateFundedArkadeCash(ArkServerInfo serverInfo, long amount)
    {
        var cash = ArkadeCash.Generate(
            serverInfo.SignerKey.ToXOnlyPubKey(),
            serverInfo.UnilateralExit,
            "tarkadecash");

        var cashAddress = cash.GetAddress(serverInfo.Network);
        await DockerHelper.SendArkdNoteTo(cashAddress.ToString(false), amount);
        return cash;
    }

    private static async Task ForcePollScript(
        VtxoSynchronizationService vtxoSync,
        string scriptHex,
        TimeSpan timeout)
    {
        var timeoutAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            await vtxoSync.PollScriptsForVtxos(new HashSet<string> { scriptHex });
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }
}
