using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
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

    [Test]
    public async Task ClaimSweepsTheNoteToTheDestination()
    {
        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);
        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var contractService = new ContractService(walletProvider, storage.ContractStorage, clientTransport);

        var serverInfo = await clientTransport.GetServerInfoAsync();
        using var cash = await CreateFundedArkadeCash(serverInfo, 100000);

        // The claiming wallet never learns about the note's contract — it only supplies an address.
        var receiverWalletId = await walletProvider.CreateTestWallet();
        var destination = (await contractService.DeriveContract(receiverWalletId, NextContractPurpose.Receive))
            .GetArkAddress();

        var cashService = new ArkadeCashService(clientTransport, safetyService, storage.IntentStorage);
        var result = await ClaimWhenFundingLands(cashService, cash, destination, TimeSpan.FromSeconds(30));

        Assert.That(result.Swept, Is.EqualTo(100000UL), "The whole note should be swept");
        Assert.That(result.Unclaimed, Is.Empty, "Nothing should be left behind at the note's address");

        // The funds really moved: the destination holds them, and the note's address is drained.
        var atDestination = await SnapshotScript(clientTransport, destination.ScriptPubKey.ToHex());
        Assert.That(atDestination.Where(v => !v.IsSpent()).Sum(v => (long)v.Amount), Is.EqualTo(100000L),
            "Destination should hold the swept amount");

        var atNote = await SnapshotScript(clientTransport, cash.GetAddress(serverInfo.Network).ScriptPubKey.ToHex());
        Assert.That(atNote.Where(v => !v.IsSpent()).ToList(), Is.Empty,
            "The note's address should be drained after the claim");

        // A second claim of a spent note reports rather than throws.
        var second = await cashService.ClaimAsync(cash, destination);
        Assert.That(second.Swept, Is.EqualTo(0UL));
        Assert.That(second.Unclaimed.Select(v => v.Reason),
            Is.All.EqualTo(ArkadeCashUnclaimedReason.AlreadySpent));
    }

    /// <summary>
    /// Claims once the arkd note funding this ArkadeCash is visible to the indexer. A claim that finds
    /// nothing yet is not an error — it sweeps nothing and is retried.
    /// </summary>
    private static async Task<ArkadeCashClaimResult> ClaimWhenFundingLands(
        ArkadeCashService cashService,
        ArkadeCash cash,
        ArkAddress destination,
        TimeSpan timeout)
    {
        var timeoutAt = DateTimeOffset.UtcNow + timeout;
        var result = new ArkadeCashClaimResult(0, []);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            result = await cashService.ClaimAsync(cash, destination);
            if (result.Swept > 0 || result.Unclaimed.Count > 0)
                return result;
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        return result;
    }

    private static async Task<List<ArkVtxo>> SnapshotScript(GrpcClientTransport transport, string scriptHex)
    {
        var vtxos = new List<ArkVtxo>();
        await foreach (var vtxo in transport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { scriptHex }))
            vtxos.Add(vtxo);
        return vtxos;
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
