using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Extensions;
using NArk.Core.Services;
using NArk.Safety.AsyncKeyedLock;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;

namespace NArk.Tests.End2End;

public class ArkCashTests
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
        var cash = await CreateFundedArkCash(serverInfo, 100000);

        var receiverWalletId = await walletProvider.CreateTestWallet();
        await contractService.ImportContract(receiverWalletId, cash.ToContract(serverInfo.Network));

        var cashScript = cash.GetAddress(serverInfo.Network).ScriptPubKey.ToHex();
        await ForcePollScript(vtxoSync, cashScript, TimeSpan.FromSeconds(15));

        var receiverUnspent = await storage.VtxoStorage.GetVtxos(
            walletIds: [receiverWalletId],
            includeSpent: false);
        var receiverAmount = receiverUnspent.Select(v => v.Amount).Aggregate(0UL, (acc, x) => acc + x);
        
        Assert.That(receiverUnspent.Count, Is.EqualTo(1),
            "Receiver should have at least one unspent ArkCash VTXO");
        Assert.That(receiverAmount, Is.EqualTo(100000UL),
            "Receiver unspent amount should be greater than zero");
    }

    private static async Task<ArkCash> CreateFundedArkCash(ArkServerInfo serverInfo, long amount)
    {
        var cash = ArkCash.Generate(
            serverInfo.SignerKey.ToXOnlyPubKey(),
            serverInfo.UnilateralExit,
            "tarkcash");

        var cashAddress = cash.GetAddress(serverInfo.Network);
        var sendResult = await DockerHelper.ArkSend(amount, cashAddress.ToString(false), allowNonZeroExit: true);
        if (!sendResult.IsSuccess)
            throw new InvalidOperationException(
                $"ark send failed (exit={sendResult.ExitCode}): stdout={sendResult.StandardOutput}, stderr={sendResult.StandardError}");

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
