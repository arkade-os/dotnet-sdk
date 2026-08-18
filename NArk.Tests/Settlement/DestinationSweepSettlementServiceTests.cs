using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.Wallets;
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
public class DestinationSweepSettlementServiceTests
{
    private const string WalletId = "test-wallet";

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private ISpendingService _spendingService = null!;
    private IContractService _contractService = null!;
    private IClientTransport _transport = null!;
    private IOnchainService _onchainService = null!;

    [SetUp]
    public void SetUp()
    {
        _spendingService = Substitute.For<ISpendingService>();
        _contractService = Substitute.For<IContractService>();
        _transport = Substitute.For<IClientTransport>();
        _onchainService = Substitute.For<IOnchainService>();

        _spendingService.Spend(Arg.Any<string>(), Arg.Any<ArkTxOut[]>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<IExtensionPacket>?>())
            .Returns(Task.FromResult(uint256.One));
        _spendingService.Spend(Arg.Any<string>(), Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<IExtensionPacket>?>())
            .Returns(Task.FromResult(uint256.One));

        _contractService.DeriveContract(Arg.Any<string>(), Arg.Any<NextContractPurpose>(),
                Arg.Any<ContractActivityState>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkContract>(TestContract()));
    }

    private DestinationSweepSettlementService CreateService(
        bool enableCollaborativeExit = false,
        IOnchainService? onchainService = null) =>
        new(
            _spendingService,
            _contractService,
            _transport,
            Options.Create(new SettlementOptions { EnableCollaborativeExit = enableCollaborativeExit }),
            onchainService);

    private static GenericArkContract TestContract() =>
        new(TestServerKey, [new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE])]);

    private static string TestArkAddress() => TestContract().GetArkAddress().ToString(false);

    [Test]
    public void CanSettle_ArkadeBtc()
    {
        Assert.That(CreateService().CanSettle(SettlementDestination.Ark(TestArkAddress())), Is.True);
    }

    [Test]
    public void CannotSettle_ArkadeIssuedAssets()
    {
        // Settlement amounts are denominated in satoshis, so an asset balance needs its own rail.
        Assert.That(
            CreateService().CanSettle(SettlementDestination.ArkAsset(TestArkAddress(), "some-asset")),
            Is.False);
    }

    [Test]
    public void CannotSettle_OnchainBitcoin_ByDefault()
    {
        // Left to the chain-swap rail unless the application opts into collaborative exits.
        Assert.That(
            CreateService(onchainService: _onchainService).CanSettle(SettlementDestination.BitcoinOnchain("bcrt1qexample")),
            Is.False);
    }

    [Test]
    public void CanSettle_OnchainBitcoin_WhenCollaborativeExitIsEnabled()
    {
        Assert.That(
            CreateService(enableCollaborativeExit: true, onchainService: _onchainService)
                .CanSettle(SettlementDestination.BitcoinOnchain("bcrt1qexample")),
            Is.True);
    }

    [Test]
    public void CannotSettle_OnchainBitcoin_WithoutAnOnchainService()
    {
        Assert.That(
            CreateService(enableCollaborativeExit: true)
                .CanSettle(SettlementDestination.BitcoinOnchain("bcrt1qexample")),
            Is.False);
    }

    [Test]
    public async Task SpendsToTheArkadeDestination()
    {
        var address = TestArkAddress();

        var result = await CreateService().SettleAsync(
            new SettlementRequest(WalletId, 75_000, SettlementDestination.Ark(address)));

        await _spendingService.Received(1).Spend(
            WalletId,
            Arg.Is<ArkTxOut[]>(outputs =>
                outputs.Length == 1 &&
                outputs[0].Type == ArkTxOutType.Vtxo &&
                outputs[0].Value == Money.Satoshis(75_000)),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());

        Assert.Multiple(() =>
        {
            Assert.That(result.SourceAmountSats, Is.EqualTo(75_000));
            Assert.That(result.DestinationAmountSats, Is.EqualTo(75_000));
            Assert.That(result.TransactionId, Is.EqualTo(uint256.One));
        });
    }

    [Test]
    public async Task SpendsTheSuppliedCoins_WhenAPolicyPickedThem()
    {
        var coin = CreateCoin();

        await CreateService().SettleAsync(
            new SettlementRequest(WalletId, 100_000, SettlementDestination.Ark(TestArkAddress()), [coin]));

        await _spendingService.Received(1).Spend(
            WalletId,
            Arg.Is<ArkCoin[]>(coins => coins.Length == 1 && coins[0] == coin),
            Arg.Any<ArkTxOut[]>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());
    }

    [Test]
    public async Task DerivesAFreshAddress_ForASelfDestination()
    {
        await CreateService().SettleAsync(
            new SettlementRequest(WalletId, 30_000, SettlementDestination.ArkSelf()));

        await _contractService.Received(1).DeriveContract(
            WalletId,
            NextContractPurpose.SendToSelf,
            Arg.Any<ContractActivityState>(),
            Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Throws_ForAnUnsupportedDestination()
    {
        // A destination the SDK knows nothing about — the application's own rail handles it.
        var destination = new SettlementDestination("tron", "USDT", "TXexample");

        Assert.ThrowsAsync<SettlementNotSupportedException>(() =>
            CreateService().SettleAsync(new SettlementRequest(WalletId, 30_000, destination)));
    }

    [Test]
    public void Throws_ForANonPositiveAmount()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreateService().SettleAsync(
                new SettlementRequest(WalletId, 0, SettlementDestination.Ark(TestArkAddress()))));
    }

    private static ArkCoin CreateCoin()
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var contract = new GenericArkContract(TestServerKey, [script]);

        return new ArkCoin(
            walletIdentifier: WalletId,
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, 0),
            txOut: new TxOut(Money.Satoshis(100_000), Script.FromHex("a914" + new string('0', 40) + "87")),
            signerDescriptor: null,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false);
    }
}
