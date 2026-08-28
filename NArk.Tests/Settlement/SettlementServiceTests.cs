using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Settlement;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests.Settlement;

[TestFixture]
public class SettlementServiceTests
{
    private const string WalletId = "test-wallet";
    private const string ScriptHex = "a914" + "0000000000000000000000000000000000000000" + "87";
    private const long CoinAmount = 100_000;
    private const long AssetCarrierSats = 1_000;
    private const string Usdt0 = "usdt0-asset-id";

    private static readonly SettlementDestination Destination = SettlementDestination.Ark("ark1qexample");
    private static readonly TimeHeight CurrentTime = new(DateTimeOffset.UtcNow, 800_000);

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private ISettlementConfigProvider _configProvider = null!;
    private ISettlementService _rail = null!;
    private ISpendingService _spendingService = null!;
    private IIntentStorage _intentStorage = null!;
    private IVtxoStorage _vtxoStorage = null!;
    private IContractStorage _contractStorage = null!;
    private IBitcoinBlockchain _blockchain = null!;
    private IEventHandler<PostSettlementActionEvent> _eventHandler = null!;

    [SetUp]
    public void SetUp()
    {
        _configProvider = Substitute.For<ISettlementConfigProvider>();
        _rail = Substitute.For<ISettlementService>();
        _spendingService = Substitute.For<ISpendingService>();
        _intentStorage = Substitute.For<IIntentStorage>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _contractStorage = Substitute.For<IContractStorage>();
        _blockchain = Substitute.For<IBitcoinBlockchain>();
        _eventHandler = Substitute.For<IEventHandler<PostSettlementActionEvent>>();

        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>(
                [new SettlementConfig(WalletId, Destination, 50_000)]));

        _rail.Available.Returns(true);
        _rail.CanSettle(Arg.Any<SettlementDestination>()).Returns(true);
        _rail.SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                new SettlementResult("transfer-1", call.Arg<SettlementRequest>().Amount,
                    call.Arg<SettlementRequest>().Amount, 0)));

        _blockchain.GetChainTime(Arg.Any<CancellationToken>()).Returns(Task.FromResult(CurrentTime));

        _spendingService.GetAvailableCoins(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkCoin>>(new HashSet<ArkCoin> { CreateCoin() }));

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
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([CreateContractEntity()]));
    }

    [Test]
    public async Task SettlesConfiguredWallet_WhenItsVtxosChange()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(request =>
                request.WalletId == WalletId &&
                request.Amount == CoinAmount &&
                request.Destination == Destination),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task LeavesAssetCarriersOutOfTheBtcBalance()
    {
        // The asset VTXO's dust is the asset's carrier, not spendable BTC: settling it would
        // take the asset along, so only the plain coin funds a BTC settlement.
        _spendingService.GetAvailableCoins(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkCoin>>(
                new HashSet<ArkCoin> { CreateCoin(), CreateAssetCoin(Usdt0, 750_000) }));

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(request =>
                request.Amount == CoinAmount &&
                request.SourceAsset == SettlementAssets.Btc),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SettlesAnAssetBalance_OnAnAssetRule()
    {
        var assetDestination = SettlementDestination.ArkAsset("ark1qassetpayout", Usdt0);
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>(
                [new SettlementConfig(WalletId, assetDestination, 500_000, SourceAsset: Usdt0)]));

        // No plain BTC coin at all: an asset rule must still fire.
        _spendingService.GetAvailableCoins(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkCoin>>(
                new HashSet<ArkCoin> { CreateAssetCoin(Usdt0, 750_000) }));

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(request =>
                request.SourceAsset == Usdt0 &&
                request.Amount == 750_000 &&
                request.Destination == assetDestination),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SpendsBtcAndAssetRemaindersIndependently()
    {
        var assetDestination = SettlementDestination.ArkAsset("ark1qassetpayout", Usdt0);
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>(
            [
                new SettlementConfig(WalletId, Destination, 0),
                new SettlementConfig(WalletId, assetDestination, 0, SourceAsset: Usdt0)
            ]));

        _spendingService.GetAvailableCoins(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkCoin>>(
                new HashSet<ArkCoin> { CreateCoin(), CreateAssetCoin(Usdt0, 750_000) }));

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        // Settling the whole BTC balance must not stop the asset plan behind it: the two
        // denominations draw on separate remainders.
        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(r => r.SourceAsset == SettlementAssets.Btc && r.Amount == CoinAmount),
            Arg.Any<CancellationToken>());
        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(r => r.SourceAsset == Usdt0 && r.Amount == 750_000),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task DoesNotSettle_WhenAGateBlocksTheWallet()
    {
        var gate = Substitute.For<ISettlementGate>();
        gate.IsBlockedAsync(WalletId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        using var service = CreateService(gates: [gate]);
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _rail.DidNotReceive().SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task DoesNotSettle_WhenNoPolicyYieldsAPlan()
    {
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>([]));

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _rail.DidNotReceive().SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecutesEveryPlanAPolicyYields()
    {
        var first = SettlementDestination.Ark("ark1qfirst");
        var second = SettlementDestination.Ark("ark1qsecond");
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>(
            [
                new SettlementConfig(WalletId, first, 0, MaxAmount: 40_000),
                new SettlementConfig(WalletId, second, 10_000, MaxAmount: 25_000)
            ]));

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(r => r.Destination == first && r.Amount == 40_000),
            Arg.Any<CancellationToken>());
        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(r => r.Destination == second && r.Amount == 25_000),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SkipsAPlanThatNoLongerFitsTheRemainingBalance()
    {
        // Both rules plan the full balance; committing the first leaves nothing for the
        // second, which must not be executed on top of it.
        var first = SettlementDestination.Ark("ark1qfirst");
        var second = SettlementDestination.Ark("ark1qsecond");
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>(
            [
                new SettlementConfig(WalletId, first, 0),
                new SettlementConfig(WalletId, second, 10_000)
            ]));

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(r => r.Destination == first && r.Amount == CoinAmount),
            Arg.Any<CancellationToken>());
        await _rail.DidNotReceive().SettleAsync(
            Arg.Is<SettlementRequest>(r => r.Destination == second),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task LeavesTheBalanceForLaterPlans_WhenASettlementFails()
    {
        var first = SettlementDestination.Ark("ark1qfirst");
        var second = SettlementDestination.Ark("ark1qsecond");
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>(
            [
                new SettlementConfig(WalletId, first, 0),
                new SettlementConfig(WalletId, second, 10_000)
            ]));

        _rail.SettleAsync(
                Arg.Is<SettlementRequest>(r => r.Destination == first), Arg.Any<CancellationToken>())
            .Returns<Task<SettlementResult>>(_ => throw new InvalidOperationException("rail rejected the transfer"));

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        // The failed plan committed nothing, so the whole balance is still available.
        await _rail.Received(1).SettleAsync(
            Arg.Is<SettlementRequest>(r => r.Destination == second && r.Amount == CoinAmount),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RaisesAFailedEvent_AndKeepsProcessing_WhenTheRailThrows()
    {
        _rail.SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<SettlementResult>>(_ => throw new InvalidOperationException("provider rejected the transfer"));

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _eventHandler.Received(1).HandleAsync(
            Arg.Is<PostSettlementActionEvent>(e =>
                e.State == ActionState.Failed &&
                e.Result == null &&
                e.FailReason == "provider rejected the transfer"),
            Arg.Any<CancellationToken>());

        // The loop must survive a failing rail: a later trigger is still processed.
        RaiseVtxoChanged();
        await Task.Delay(300);

        await _rail.Received(2).SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RaisesASuccessEvent_OnSettlement()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        RaiseVtxoChanged();
        await Task.Delay(300);

        await _eventHandler.Received(1).HandleAsync(
            Arg.Is<PostSettlementActionEvent>(e =>
                e.State == ActionState.Successful &&
                e.Result!.TransferId == "transfer-1"),
            Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    private void RaiseVtxoChanged() =>
        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, CreateVtxo());

    private SettlementService CreateService(IEnumerable<ISettlementGate>? gates = null) =>
        new(
            [new BalanceThresholdSettlementPolicy(_configProvider)],
            gates ?? [],
            [],
            new CompositeSettlementService([_rail]),
            _configProvider,
            _spendingService,
            _intentStorage,
            _vtxoStorage,
            _contractStorage,
            _blockchain,
            Options.Create(new SettlementOptions
            {
                Debounce = TimeSpan.Zero,
                HeartbeatInterval = TimeSpan.Zero
            }),
            [_eventHandler]);

    private static ArkVtxo CreateVtxo() =>
        new(
            Script: ScriptHex,
            TransactionId: uint256.One.ToString(),
            TransactionOutputIndex: 0,
            Amount: CoinAmount,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(30),
            ExpiresAtHeight: null);

    private static ArkContractEntity CreateContractEntity() =>
        new(
            Script: ScriptHex,
            ActivityState: ContractActivityState.Active,
            Type: "generic",
            AdditionalData: new Dictionary<string, string>(),
            WalletIdentifier: WalletId,
            CreatedAt: DateTimeOffset.UtcNow);

    private static ArkCoin CreateAssetCoin(string assetId, ulong amount) =>
        CreateCoin(
            outPoint: new OutPoint(uint256.One, 1),
            amountSats: AssetCarrierSats,
            assets: [new VtxoAsset(assetId, amount)]);

    private static ArkCoin CreateCoin() => CreateCoin(new OutPoint(uint256.One, 0), CoinAmount, null);

    private static ArkCoin CreateCoin(OutPoint outPoint, long amountSats, IReadOnlyList<VtxoAsset>? assets)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var contract = new GenericArkContract(TestServerKey, [script]);

        return new ArkCoin(
            walletIdentifier: WalletId,
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: outPoint,
            txOut: new TxOut(Money.Satoshis(amountSats), Script.FromHex(ScriptHex)),
            signerDescriptor: null,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false,
            assets: assets);
    }
}
