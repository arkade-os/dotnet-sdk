using System.Runtime.CompilerServices;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Assets;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Settlement;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests.Settlement;

[TestFixture]
public class ArkAssetSettlementServiceTests
{
    private const string WalletId = "test-wallet";
    private const string Usdt0 = "usdt0-asset-id";
    private const long DustSats = 1_000;

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private ISpendingService _spendingService = null!;
    private IContractService _contractService = null!;
    private IClientTransport _transport = null!;

    [SetUp]
    public void SetUp()
    {
        _spendingService = Substitute.For<ISpendingService>();
        _contractService = Substitute.For<IContractService>();
        _transport = Substitute.For<IClientTransport>();

        _spendingService.Spend(Arg.Any<string>(), Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<IExtensionPacket>?>())
            .Returns(Task.FromResult(uint256.One));

        _contractService.DeriveContract(Arg.Any<string>(), Arg.Any<NextContractPurpose>(),
                Arg.Any<ContractActivityState>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkContract>(TestContract()));

        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StubServerInfo()));

        SetAvailableCoins(AssetCoin(750_000), BtcCoin(100_000));
    }

    private ArkAssetSettlementService CreateService() =>
        new(_spendingService, _contractService, _transport);

    private void SetAvailableCoins(params ArkCoin[] coins) =>
        _spendingService.GetAvailableCoins(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkCoin>>(new HashSet<ArkCoin>(coins)));

    [Test]
    public void CanSettle_AnArkadeAssetDestination()
    {
        Assert.That(
            CreateService().CanSettle(SettlementDestination.ArkAsset(TestArkAddress(), Usdt0)),
            Is.True);
    }

    [Test]
    public void CannotSettle_ArkadeBtc()
    {
        // BTC belongs to the destination sweep; this rail moves assets only.
        Assert.That(CreateService().CanSettle(SettlementDestination.Ark(TestArkAddress())), Is.False);
    }

    [Test]
    public void CannotSettle_AForeignNetwork()
    {
        Assert.That(
            CreateService().CanSettle(new SettlementDestination("tron", "USDT", "TXexample")),
            Is.False);
    }

    [Test]
    public async Task SendsTheWholeAssetBalance_AsOneAssetOutput()
    {
        var result = await CreateService().SettleAsync(new SettlementRequest(
            WalletId, 750_000, SettlementDestination.ArkAsset(TestArkAddress(), Usdt0), Usdt0));

        await _spendingService.Received(1).Spend(
            WalletId,
            // Only the carrier is spent: its dust covers the single asset output.
            Arg.Is<ArkCoin[]>(coins => coins.Length == 1 && coins[0].Assets!.Count == 1),
            Arg.Is<ArkTxOut[]>(outputs =>
                outputs.Length == 1 &&
                outputs[0].Value == Money.Satoshis(DustSats) &&
                outputs[0].Assets!.Single().AssetId == Usdt0 &&
                outputs[0].Assets!.Single().Amount == 750_000),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());

        Assert.Multiple(() =>
        {
            Assert.That(result.SourceAmount, Is.EqualTo(750_000));
            Assert.That(result.DestinationAtomicAmount, Is.EqualTo(750_000));
            Assert.That(result.DestinationAmountSats, Is.EqualTo(DustSats));
        });
    }

    [Test]
    public async Task ReturnsTheAssetRemainderAsChange()
    {
        await CreateService().SettleAsync(new SettlementRequest(
            WalletId, 500_000, SettlementDestination.ArkAsset(TestArkAddress(), Usdt0), Usdt0));

        // Without the change output the asset packet would show 750 000 in and 500 000 out,
        // destroying the remainder.
        await _spendingService.Received(1).Spend(
            WalletId,
            Arg.Any<ArkCoin[]>(),
            Arg.Is<ArkTxOut[]>(outputs =>
                outputs.Length == 2 &&
                outputs[0].Assets!.Single().Amount == 500_000 &&
                outputs[1].Assets!.Single().Amount == 250_000),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());
    }

    [Test]
    public async Task TopsUpFromBtcCoins_WhenTheCarriersCannotFundEveryOutput()
    {
        // One carrier holding a single dust cannot fund both the payout and the asset change,
        // so the rail has to pull a plain BTC coin in.
        await CreateService().SettleAsync(new SettlementRequest(
            WalletId, 500_000, SettlementDestination.ArkAsset(TestArkAddress(), Usdt0), Usdt0));

        await _spendingService.Received(1).Spend(
            WalletId,
            Arg.Is<ArkCoin[]>(coins =>
                coins.Length == 2 &&
                coins.Any(coin => coin.Assets == null || coin.Assets.Count == 0)),
            Arg.Any<ArkTxOut[]>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());
    }

    [Test]
    public async Task DerivesOwnAddresses_NotSendToSelf_ForThePayoutAndTheChange()
    {
        // On a wallet with an auto-sweep destination every SendToSelf derivation resolves to that
        // address. Deriving the asset payout or its change that way would send the asset there
        // instead of keeping it where the rule pointed.
        var derived = new Queue<ArkContract>([TestContract(), SecondContract()]);
        _contractService.DeriveContract(WalletId, NextContractPurpose.Receive,
                Arg.Any<ContractActivityState>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(derived.Dequeue()));

        await CreateService().SettleAsync(new SettlementRequest(
            WalletId, 500_000, new SettlementDestination(SettlementNetworks.Ark, Usdt0, null), Usdt0));

        await _contractService.Received(2).DeriveContract(WalletId, NextContractPurpose.Receive,
            Arg.Any<ContractActivityState>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
        await _contractService.DidNotReceive().DeriveContract(WalletId, NextContractPurpose.SendToSelf,
            Arg.Any<ContractActivityState>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
        await _contractService.DidNotReceive().DeriveContract(WalletId, Arg.Any<NextContractPurpose>(),
            Arg.Any<ArkContract[]>(), Arg.Any<ContractActivityState>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());

        // Payout and change land on two different owned addresses.
        await _spendingService.Received(1).Spend(
            WalletId,
            Arg.Any<ArkCoin[]>(),
            Arg.Is<ArkTxOut[]>(outputs =>
                outputs.Length == 2 &&
                outputs[0].ScriptPubKey.ToHex() != outputs[1].ScriptPubKey.ToHex()),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());
    }

    [Test]
    public void Throws_RatherThanSpendBeyondThePinnedCoins()
    {
        // Pinned coins are the caller's decision about what may be spent. One carrier holding a
        // single dust cannot fund both the payout and the asset change, and topping it up from
        // coins the caller did not pin would spend outside that mandate.
        var carrier = AssetCoin(750_000);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().SettleAsync(new SettlementRequest(
                WalletId, 500_000, SettlementDestination.ArkAsset(TestArkAddress(), Usdt0), Usdt0,
                Coins: [carrier])));

        Assert.That(ex!.Message, Does.Contain("carry the asset outputs"));
    }

    [Test]
    public void Throws_WhenTheRailWouldHaveToConvert()
    {
        // Handing USDT0 to a destination expecting another asset is a conversion, not a transfer.
        Assert.ThrowsAsync<SettlementNotSupportedException>(() =>
            CreateService().SettleAsync(new SettlementRequest(
                WalletId, 500_000, SettlementDestination.ArkAsset(TestArkAddress(), "other-asset"), Usdt0)));
    }

    [Test]
    public void Throws_WhenTheWalletHoldsTooLittleOfTheAsset()
    {
        SetAvailableCoins(AssetCoin(100_000));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().SettleAsync(new SettlementRequest(
                WalletId, 500_000, SettlementDestination.ArkAsset(TestArkAddress(), Usdt0), Usdt0)));
    }

    [Test]
    public void Throws_ForANonPositiveAmount()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreateService().SettleAsync(new SettlementRequest(
                WalletId, 0, SettlementDestination.ArkAsset(TestArkAddress(), Usdt0), Usdt0)));
    }

    private static GenericArkContract TestContract() =>
        new(TestServerKey, [new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE])]);

    private static GenericArkContract SecondContract() =>
        new(TestServerKey, [new GenericTapScript([Op.GetPushOp(2), OpcodeType.OP_TRUE])]);

    private static string TestArkAddress() => TestContract().GetArkAddress().ToString(false);

    private static ArkCoin AssetCoin(ulong amount) =>
        CreateCoin(new OutPoint(uint256.One, 1), DustSats, [new VtxoAsset(Usdt0, amount)]);

    private static ArkCoin BtcCoin(long sats) =>
        CreateCoin(new OutPoint(uint256.One, 0), sats, null);

    private static ArkCoin CreateCoin(OutPoint outPoint, long sats, IReadOnlyList<VtxoAsset>? assets)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);

        return new ArkCoin(
            walletIdentifier: WalletId,
            contract: new GenericArkContract(TestServerKey, [script]),
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: outPoint,
            txOut: new TxOut(Money.Satoshis(sats), Script.FromHex("a914" + new string('0', 40) + "87")),
            signerDescriptor: null,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false,
            assets: assets);
    }

    // The rail reads only Dust off the server info, and constructing a real one needs a long
    // tail of protocol primitives.
    private static ArkServerInfo StubServerInfo()
    {
        var info = (ArkServerInfo)RuntimeHelpers.GetUninitializedObject(typeof(ArkServerInfo));
        typeof(ArkServerInfo).GetProperty(nameof(ArkServerInfo.Dust))!
            .SetValue(info, Money.Satoshis(DustSats));
        return info;
    }
}
