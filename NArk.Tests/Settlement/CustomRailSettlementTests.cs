using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.VTXOs;
using NArk.Core;
using NArk.Core.Assets;
using NArk.Core.Contracts;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Settlement;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests.Settlement;

/// <summary>
/// Covers the extension point an application uses to settle somewhere the SDK has never
/// heard of — an EVM chain, a stablecoin desk, an exchange deposit — alongside the
/// built-in rails, in either denomination.
/// </summary>
[TestFixture]
public class CustomRailSettlementTests
{
    private const string WalletId = "test-wallet";
    private const string ScriptHex = "a914" + "0000000000000000000000000000000000000000" + "87";
    private const string Usdt0 = "usdt0-asset-id";
    private const long BtcCoinAmount = 100_000;
    private const long AssetCarrierSats = 1_000;
    private const ulong AssetBalance = 750_000;

    // An address shape the SDK cannot parse, on a network it does not know.
    private static readonly SettlementDestination EvmUsdc =
        new("base", "USDC", "0x71C7656EC7ab88b098defB751B7401B5f6d8976F");

    private static readonly TimeHeight CurrentTime = new(DateTimeOffset.UtcNow, 800_000);

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private RecordingRail _appRail = null!;
    private ISettlementConfigProvider _configProvider = null!;
    private ISpendingService _spendingService = null!;
    private IContractService _contractService = null!;
    private IClientTransport _transport = null!;
    private IIntentStorage _intentStorage = null!;
    private IVtxoStorage _vtxoStorage = null!;
    private IContractStorage _contractStorage = null!;
    private IBitcoinBlockchain _blockchain = null!;
    private IEventHandler<PostSettlementActionEvent> _eventHandler = null!;

    [SetUp]
    public void SetUp()
    {
        _appRail = new RecordingRail();
        _configProvider = Substitute.For<ISettlementConfigProvider>();
        _spendingService = Substitute.For<ISpendingService>();
        _contractService = Substitute.For<IContractService>();
        _transport = Substitute.For<IClientTransport>();
        _intentStorage = Substitute.For<IIntentStorage>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _contractStorage = Substitute.For<IContractStorage>();
        _blockchain = Substitute.For<IBitcoinBlockchain>();
        _eventHandler = Substitute.For<IEventHandler<PostSettlementActionEvent>>();

        _blockchain.GetChainTime(Arg.Any<CancellationToken>()).Returns(Task.FromResult(CurrentTime));
        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(StubServerInfo()));

        _spendingService.GetAvailableCoins(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkCoin>>(
                new HashSet<ArkCoin> { BtcCoin(), AssetCoin(Usdt0, AssetBalance) }));

        _intentStorage.GetLockedVtxoOutpoints(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<OutPoint>>([]));

        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Any<bool?>(),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                scope: Arg.Any<ContractScope?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([ContractEntity()]));

        Configure(new SettlementConfig(WalletId, EvmUsdc, 500_000, SourceAsset: Usdt0));
    }

    [Test]
    public void BuiltInRailsDeclineAForeignDestination_AndRoutingFindsTheApplicationRail()
    {
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(Sweep().CanSettle(EvmUsdc), Is.False);
            Assert.That(AssetRail().CanSettle(EvmUsdc), Is.False);
            // Registration order puts the built-ins first; neither claims the destination.
            Assert.That(composite.Resolve(EvmUsdc), Is.SameAs(_appRail));
        });
    }

    [Test]
    public async Task SettlesAnArkadeAssetToAnEvmDestination()
    {
        await RunEngine();

        var request = _appRail.Requests.Single();
        Assert.Multiple(() =>
        {
            // The source side stays the Arkade asset the threshold measured; the destination
            // asset is whatever the rail delivers.
            Assert.That(request.SourceAsset, Is.EqualTo(Usdt0));
            Assert.That(request.Amount, Is.EqualTo((long)AssetBalance));
            Assert.That(request.Destination.Network, Is.EqualTo("base"));
            Assert.That(request.Destination.Asset, Is.EqualTo("USDC"));
            // An address the SDK never parses reaches the rail exactly as configured.
            Assert.That(request.Destination.Address, Is.EqualTo(EvmUsdc.Address));
        });
    }

    [Test]
    public async Task SettlesBtcToAnEvmDestination()
    {
        Configure(new SettlementConfig(WalletId, EvmUsdc, 50_000));

        await RunEngine();

        var request = _appRail.Requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.SourceAsset, Is.EqualTo(SettlementAssets.Btc));
            // Satoshis only: the asset carrier's dust is not part of the BTC balance.
            Assert.That(request.Amount, Is.EqualTo(BtcCoinAmount));
        });
    }

    [Test]
    public async Task LeavesTheCoinsToTheApplicationRail()
    {
        await RunEngine();

        // The engine plans and routes; moving value on a foreign network is entirely the
        // rail's business, so the SDK must not have spent anything itself.
        await _spendingService.DidNotReceive().Spend(
            Arg.Any<string>(), Arg.Any<ArkTxOut[]>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());
        await _spendingService.DidNotReceive().Spend(
            Arg.Any<string>(), Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());
    }

    [Test]
    public async Task ReportsWhatTheRailDelivered()
    {
        _appRail.DestinationAtomicAmount = 7_412_500;
        _appRail.FeesPaidSats = 320;

        await RunEngine();

        await _eventHandler.Received(1).HandleAsync(
            Arg.Is<PostSettlementActionEvent>(e =>
                e.State == ActionState.Successful &&
                e.Request.SourceAsset == Usdt0 &&
                e.Result!.SourceAmount == (long)AssetBalance &&
                e.Result.DestinationAtomicAmount == 7_412_500 &&
                e.Result.FeesPaidSats == 320),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SettlesBtcAndAnAssetToTwoDifferentRails_InOnePass()
    {
        var arkPayout = SettlementDestination.Ark(TestArkAddress());
        Configure(
            new SettlementConfig(WalletId, arkPayout, 50_000),
            new SettlementConfig(WalletId, EvmUsdc, 500_000, SourceAsset: Usdt0));

        _spendingService.Spend(Arg.Any<string>(), Arg.Any<ArkTxOut[]>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<IExtensionPacket>?>())
            .Returns(Task.FromResult(uint256.One));

        await RunEngine();

        // BTC went out through the built-in sweep, the asset through the application rail,
        // each drawing on its own remainder.
        await _spendingService.Received(1).Spend(
            WalletId,
            Arg.Is<ArkTxOut[]>(outputs => outputs.Single().Value == Money.Satoshis(BtcCoinAmount)),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<IExtensionPacket>?>());

        Assert.That(_appRail.Requests.Single().Amount, Is.EqualTo((long)AssetBalance));
    }

    [Test]
    public void ThrowsForAForeignDestination_WhenNoRailIsRegisteredForIt()
    {
        var composite = new CompositeSettlementService([Sweep(), AssetRail()]);

        var ex = Assert.ThrowsAsync<SettlementNotSupportedException>(() =>
            composite.SettleAsync(new SettlementRequest(WalletId, 500_000, EvmUsdc, Usdt0)));

        Assert.That(ex!.Message, Does.Contain("base/USDC"));
    }

    private void Configure(params SettlementConfig[] configs) =>
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>(configs));

    private async Task RunEngine()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, Vtxo());
        await Task.Delay(300);

        await service.StopAsync(CancellationToken.None);
    }

    private SettlementService CreateService() =>
        new(
            [new BalanceThresholdSettlementPolicy(_configProvider)],
            [],
            [],
            Composite(),
            _configProvider,
            _spendingService,
            _intentStorage,
            _vtxoStorage,
            _contractStorage,
            _blockchain,
            Options.Create(new SettlementOptions { Debounce = TimeSpan.Zero, HeartbeatInterval = TimeSpan.Zero }),
            [_eventHandler]);

    // The registration AddArkSettlement() produces, plus the application's own rail.
    private CompositeSettlementService Composite() => new([Sweep(), AssetRail(), _appRail]);

    private DestinationSweepSettlementService Sweep() =>
        new(_spendingService, _contractService, _transport, Options.Create(new SettlementOptions()));

    private ArkAssetSettlementService AssetRail() =>
        new(_spendingService, _contractService, _transport);

    private sealed class RecordingRail : ISettlementService
    {
        public List<SettlementRequest> Requests { get; } = [];
        public long? DestinationAtomicAmount { get; set; }
        public long FeesPaidSats { get; set; }

        public bool Available => true;
        public string? UnavailableReason => null;

        public bool CanSettle(SettlementDestination destination) => destination.Is("base", "USDC");

        public Task<SettlementResult> SettleAsync(
            SettlementRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            return Task.FromResult(new SettlementResult(
                TransferId: "0xdeadbeef",
                SourceAmount: request.Amount,
                DestinationAmountSats: 0,
                FeesPaidSats: FeesPaidSats,
                DestinationAtomicAmount: DestinationAtomicAmount ?? request.Amount));
        }
    }

    private static string TestArkAddress() =>
        new GenericArkContract(TestServerKey, [new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE])])
            .GetArkAddress().ToString(false);

    private static ArkVtxo Vtxo() =>
        new(
            Script: ScriptHex,
            TransactionId: uint256.One.ToString(),
            TransactionOutputIndex: 0,
            Amount: BtcCoinAmount,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(30),
            ExpiresAtHeight: null);

    private static ArkContractEntity ContractEntity() =>
        new(
            Script: ScriptHex,
            ActivityState: ContractActivityState.Active,
            Type: "generic",
            AdditionalData: new Dictionary<string, string>(),
            WalletIdentifier: WalletId,
            CreatedAt: DateTimeOffset.UtcNow);

    private static ArkCoin BtcCoin() => Coin(new OutPoint(uint256.One, 0), BtcCoinAmount, null);

    private static ArkCoin AssetCoin(string assetId, ulong amount) =>
        Coin(new OutPoint(uint256.One, 1), AssetCarrierSats, [new VtxoAsset(assetId, amount)]);

    private static ArkCoin Coin(OutPoint outPoint, long sats, IReadOnlyList<VtxoAsset>? assets)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);

        return new ArkCoin(
            walletIdentifier: WalletId,
            contract: new GenericArkContract(TestServerKey, [script]),
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: outPoint,
            txOut: new TxOut(Money.Satoshis(sats), Script.FromHex(ScriptHex)),
            signerDescriptor: null,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false,
            assets: assets);
    }

    private static ArkServerInfo StubServerInfo()
    {
        var info = (ArkServerInfo)RuntimeHelpers.GetUninitializedObject(typeof(ArkServerInfo));
        typeof(ArkServerInfo).GetProperty(nameof(ArkServerInfo.Dust))!
            .SetValue(info, Money.Satoshis(AssetCarrierSats));
        return info;
    }
}
