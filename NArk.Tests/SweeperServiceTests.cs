using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Events;
using NArk.Abstractions.Extensions;
using NArk.Core.Models.Options;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Sweeper;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class SweeperServiceTests
{
    private ISweepPolicy _sweepPolicy = null!;
    private IVtxoStorage _vtxoStorage = null!;
    private ICoinService _coinService = null!;
    private IContractStorage _contractStorage = null!;
    private ISpendingService _spendingService = null!;
    private IChainTimeProvider _chainTimeProvider = null!;

    private static readonly TimeHeight CurrentTime = new(DateTimeOffset.UtcNow, 800_000);

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    [SetUp]
    public void SetUp()
    {
        _sweepPolicy = Substitute.For<ISweepPolicy>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _contractStorage = Substitute.For<IContractStorage>();
        _coinService = Substitute.For<ICoinService>();
        _spendingService = Substitute.For<ISpendingService>();
        _chainTimeProvider = Substitute.For<IChainTimeProvider>();

        _chainTimeProvider.GetChainTime(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CurrentTime));

        // Default: vtxoStorage returns no vtxos for queries
        _vtxoStorage.GetVtxos(
                scripts: Arg.Any<IReadOnlyCollection<string>?>(),
                outpoints: Arg.Any<IReadOnlyCollection<OutPoint>?>(),
                walletIds: Arg.Any<string[]?>(),
                includeSpent: Arg.Any<bool>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkVtxo>>([]));

        // Default: contractStorage returns no contracts
        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Any<bool?>(),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([]));

        // Default: policy returns nothing
        _sweepPolicy.SweepAsync(Arg.Any<IEnumerable<ArkCoin>>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<ArkCoin>()));

        // Default: spendingService returns a dummy txid
        _spendingService.Spend(
                Arg.Any<string>(), Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(uint256.One));
    }

    [Test]
    public async Task SweepsCoinsRecommendedByPolicy()
    {
        // Arrange
        var scriptHex = "a914" + new string('0', 40) + "87";
        var vtxo = CreateVtxo(scriptHex, swept: false);
        var contract = CreateContractEntity(scriptHex);
        var coin = CreateArkCoin(scriptHex, swept: false);

        SetupVtxoTrigger(vtxo, contract, coin);

        _sweepPolicy.SweepAsync(Arg.Any<IEnumerable<ArkCoin>>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new[] { coin }));

        await using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        // Act: raise VtxosChanged event
        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);

        // Allow the channel-based loop to process
        await Task.Delay(200);

        // Assert: spendingService.Spend should have been called with the coin
        await _spendingService.Received(1).Spend(
            coin.WalletIdentifier,
            Arg.Is<ArkCoin[]>(coins => coins.Length == 1 && coins[0] == coin),
            Arg.Any<ArkTxOut[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkipsCoins_WhenNotSpendableOffchain()
    {
        // Arrange: coin with Swept=true cannot be spent offchain
        var scriptHex = "a914" + new string('0', 40) + "87";
        var vtxo = CreateVtxo(scriptHex, swept: false);
        var contract = CreateContractEntity(scriptHex);
        var sweptCoin = CreateArkCoin(scriptHex, swept: true);

        SetupVtxoTrigger(vtxo, contract, sweptCoin);

        // Policy still recommends the coin, but Sweep should skip it
        _sweepPolicy.SweepAsync(Arg.Any<IEnumerable<ArkCoin>>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new[] { sweptCoin }));

        await using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        // Act
        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);
        await Task.Delay(200);

        // Assert: spendingService.Spend should NOT have been called
        await _spendingService.DidNotReceive().Spend(
            Arg.Any<string>(), Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandlesEmptyPolicyResult()
    {
        // Arrange
        var scriptHex = "a914" + new string('0', 40) + "87";
        var vtxo = CreateVtxo(scriptHex, swept: false);
        var contract = CreateContractEntity(scriptHex);
        var coin = CreateArkCoin(scriptHex, swept: false);

        SetupVtxoTrigger(vtxo, contract, coin);

        // Policy returns no coins to sweep
        _sweepPolicy.SweepAsync(Arg.Any<IEnumerable<ArkCoin>>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(Array.Empty<ArkCoin>()));

        await using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        // Act
        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);
        await Task.Delay(200);

        // Assert: spendingService.Spend should NOT have been called
        await _spendingService.DidNotReceive().Spend(
            Arg.Any<string>(), Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>(), Arg.Any<CancellationToken>());
    }

    private SweeperService CreateService()
    {
        var options = Options.Create(new SweeperServiceOptions
        {
            ForceRefreshInterval = TimeSpan.Zero
        });

        return new SweeperService(
            [_sweepPolicy],
            _vtxoStorage,
            _coinService,
            _contractStorage,
            _spendingService,
            options,
            _chainTimeProvider,
            Array.Empty<IEventHandler<PostSweepActionEvent>>());
    }

    /// <summary>
    /// Wires up the mock chain so that when a VtxosChanged event fires for the given vtxo,
    /// the sweeper can resolve it through contracts and coin service back to an ArkCoin.
    /// </summary>
    private void SetupVtxoTrigger(ArkVtxo vtxo, ArkContractEntity contract, ArkCoin coin)
    {
        // When sweeper queries for unspent vtxos matching the script
        _vtxoStorage.GetVtxos(
                scripts: Arg.Is<IReadOnlyCollection<string>?>(s => s != null && s.Contains(vtxo.Script)),
                outpoints: Arg.Any<IReadOnlyCollection<OutPoint>?>(),
                walletIds: Arg.Any<string[]?>(),
                includeSpent: false,
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkVtxo>>([vtxo]));

        // When sweeper looks up contracts for the script
        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Is<string[]?>(s => s != null && s.Contains(contract.Script)),
                isActive: Arg.Any<bool?>(),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([contract]));

        // When sweeper builds the ArkCoin from contract + vtxo
        _coinService.GetCoin(
                Arg.Is<ArkContractEntity>(c => c.Script == contract.Script),
                Arg.Is<ArkVtxo>(v => v.TransactionId == vtxo.TransactionId),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(coin));
    }

    private static ArkVtxo CreateVtxo(string scriptHex, bool swept)
    {
        var txId = uint256.One.ToString();
        return new ArkVtxo(
            Script: scriptHex,
            TransactionId: txId,
            TransactionOutputIndex: 0,
            Amount: 100_000,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: swept,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(30),
            ExpiresAtHeight: null);
    }

    private static ArkContractEntity CreateContractEntity(string scriptHex)
    {
        return new ArkContractEntity(
            Script: scriptHex,
            ActivityState: ContractActivityState.Active,
            Type: "generic",
            AdditionalData: new Dictionary<string, string>(),
            WalletIdentifier: "test-wallet",
            CreatedAt: DateTimeOffset.UtcNow);
    }

    private static ArkCoin CreateArkCoin(string scriptHex, bool swept)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var contract = new GenericArkContract(TestServerKey, [script]);

        var outpoint = new OutPoint(uint256.One, 0);
        var txOut = new TxOut(Money.Satoshis(100_000), Script.FromHex(scriptHex));

        return new ArkCoin(
            walletIdentifier: "test-wallet",
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: swept);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }
}
