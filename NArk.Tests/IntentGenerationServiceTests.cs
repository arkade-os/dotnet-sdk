using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transport;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class IntentGenerationServiceTests
{
    private IClientTransport _clientTransport;
    private IFeeEstimator _feeEstimator;
    private ICoinService _coinService;
    private IWalletProvider _walletProvider;
    private IIntentStorage _intentStorage;
    private ISafetyService _safetyService;
    private IContractStorage _contractStorage;
    private IVtxoStorage _vtxoStorage;
    private IIntentScheduler _intentScheduler;
    private IOptions<IntentGenerationServiceOptions> _options;

    private const string WalletId = "w1";
    private const string ScriptHex = "a914000000000000000000000000000000000000000087";

    [SetUp]
    public void SetUp()
    {
        _clientTransport = Substitute.For<IClientTransport>();
        _feeEstimator = Substitute.For<IFeeEstimator>();
        _coinService = Substitute.For<ICoinService>();
        _walletProvider = Substitute.For<IWalletProvider>();
        _intentStorage = Substitute.For<IIntentStorage>();
        _safetyService = Substitute.For<ISafetyService>();
        _contractStorage = Substitute.For<IContractStorage>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _intentScheduler = Substitute.For<IIntentScheduler>();

        _options = Options.Create(new IntentGenerationServiceOptions
        {
            PollInterval = TimeSpan.FromMinutes(30)
        });

        // Default: safety service returns a no-op disposable
        _safetyService.LockKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CompositeDisposable([], [])));
    }

    [Test]
    public async Task SkipsWallet_WhenWaitingToSubmitIntentExists()
    {
        SetUpVtxoAndContract();

        // BatchInProgress check returns nothing
        SetUpGetIntents(
            stateFilter: [ArkIntentState.BatchInProgress],
            result: []);

        // WaitingToSubmit/WaitingForBatch check returns one active intent
        SetUpGetIntents(
            stateFilter: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch],
            result: [CreateIntent(ArkIntentState.WaitingToSubmit)]);

        await using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        await _intentScheduler.DidNotReceive().GetIntentsToSubmit(
            Arg.Any<IReadOnlyCollection<ArkCoin>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkipsWallet_WhenWaitingForBatchIntentExists()
    {
        SetUpVtxoAndContract();

        // BatchInProgress check returns nothing
        SetUpGetIntents(
            stateFilter: [ArkIntentState.BatchInProgress],
            result: []);

        // WaitingToSubmit/WaitingForBatch check returns a WaitingForBatch intent
        SetUpGetIntents(
            stateFilter: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch],
            result: [CreateIntent(ArkIntentState.WaitingForBatch)]);

        await using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        await _intentScheduler.DidNotReceive().GetIntentsToSubmit(
            Arg.Any<IReadOnlyCollection<ArkCoin>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkipsWallet_WhenBatchInProgressIntentIsNotStale()
    {
        SetUpVtxoAndContract();

        // BatchInProgress check returns a fresh (non-stale) intent
        var freshIntent = CreateIntent(ArkIntentState.BatchInProgress, updatedAt: DateTimeOffset.UtcNow);
        SetUpGetIntents(
            stateFilter: [ArkIntentState.BatchInProgress],
            result: [freshIntent]);

        await using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        // Should not reach the WaitingToSubmit/WaitingForBatch check or the scheduler
        await _intentScheduler.DidNotReceive().GetIntentsToSubmit(
            Arg.Any<IReadOnlyCollection<ArkCoin>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessesWallet_WhenNoActivePendingIntents()
    {
        SetUpVtxoAndContract();

        // BatchInProgress check returns nothing
        SetUpGetIntents(
            stateFilter: [ArkIntentState.BatchInProgress],
            result: []);

        // WaitingToSubmit/WaitingForBatch check returns nothing
        SetUpGetIntents(
            stateFilter: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch],
            result: []);

        // CoinService returns a coin for the vtxo
        var serverKey = NArk.Core.Extensions.KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);
        var mockContract = Substitute.For<ArkContract>(serverKey);
        var mockCoin = new ArkCoin(
            WalletId,
            mockContract,
            DateTimeOffset.UtcNow,
            null,
            null,
            new OutPoint(uint256.One, 0),
            new TxOut(Money.Satoshis(10000), Script.Empty),
            null,
            CreateMockScriptBuilder(),
            null,
            null,
            new Sequence(1),
            false);

        _coinService.GetCoin(Arg.Any<ArkContractEntity>(), Arg.Any<ArkVtxo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockCoin));

        // Scheduler returns empty list (no intents to generate)
        _intentScheduler.GetIntentsToSubmit(Arg.Any<IReadOnlyCollection<ArkCoin>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkIntentSpec>>([]));

        await using var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        // Verify the scheduler was called (wallet was processed)
        await _intentScheduler.Received(1).GetIntentsToSubmit(
            Arg.Any<IReadOnlyCollection<ArkCoin>>(),
            Arg.Any<CancellationToken>());
    }

    private IntentGenerationService CreateService()
    {
        return new IntentGenerationService(
            _clientTransport,
            _feeEstimator,
            _coinService,
            _walletProvider,
            _intentStorage,
            _safetyService,
            _contractStorage,
            _vtxoStorage,
            _intentScheduler,
            _options);
    }

    private void SetUpVtxoAndContract()
    {
        var vtxo = new ArkVtxo(
            Script: ScriptHex,
            TransactionId: uint256.One.ToString(),
            TransactionOutputIndex: 0,
            Amount: 10000,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
            ExpiresAtHeight: null);

        _vtxoStorage.GetVtxos(
                scripts: Arg.Any<IReadOnlyCollection<string>?>(),
                outpoints: Arg.Any<IReadOnlyCollection<OutPoint>?>(),
                walletIds: Arg.Any<string[]?>(),
                includeSpent: false,
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkVtxo>>([vtxo]));

        var contractEntity = new ArkContractEntity(
            Script: ScriptHex,
            ActivityState: ContractActivityState.Active,
            Type: "generic",
            AdditionalData: new Dictionary<string, string>(),
            WalletIdentifier: WalletId,
            CreatedAt: DateTimeOffset.UtcNow);

        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Any<bool?>(),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([contractEntity]));
    }

    private void SetUpGetIntents(ArkIntentState[] stateFilter, IReadOnlyCollection<ArkIntent> result)
    {
        _intentStorage.GetIntents(
                walletIds: Arg.Is<string[]?>(w => w != null && w.Contains(WalletId)),
                intentTxIds: Arg.Any<string[]?>(),
                intentIds: Arg.Any<string[]?>(),
                containingInputs: Arg.Any<OutPoint[]?>(),
                states: Arg.Is<ArkIntentState[]?>(s =>
                    s != null &&
                    s.Length == stateFilter.Length &&
                    stateFilter.All(sf => s.Contains(sf))),
                validAt: Arg.Any<DateTimeOffset?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
    }

    private static ArkIntent CreateIntent(
        ArkIntentState state,
        DateTimeOffset? updatedAt = null)
    {
        return new ArkIntent(
            IntentTxId: $"intent-{state}-{Guid.NewGuid():N}",
            IntentId: null,
            WalletId: WalletId,
            State: state,
            ValidFrom: DateTimeOffset.UtcNow.AddHours(-1),
            ValidUntil: DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: updatedAt ?? DateTimeOffset.UtcNow,
            RegisterProof: "dummy",
            RegisterProofMessage: "dummy",
            DeleteProof: "dummy",
            DeleteProofMessage: "dummy",
            BatchId: null,
            CommitmentTransactionId: null,
            CancellationReason: null,
            IntentVtxos: [],
            SignerDescriptor: "dummy-signer");
    }

    private static NArk.Abstractions.Scripts.ScriptBuilder CreateMockScriptBuilder()
    {
        var sb = Substitute.For<NArk.Abstractions.Scripts.ScriptBuilder>();
        sb.BuildScript().Returns(Enumerable.Empty<Op>());
        sb.Build().Returns(new TapScript(Script.Empty, TapLeafVersion.C0));
        return sb;
    }
}
