using System.Runtime.CompilerServices;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Events;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.Batches;

// Startup reconciliation cancels intents that arkd still holds a registration for. Cancelling one
// only in storage strands that registration: every cleanup here and in IntentGenerationService
// filters on the active states, so nothing looks at it again. arkd then collects two forfeits for
// the one VTXO and fails the round — for every participant, until the coin is spent or expires.
[TestFixture]
public class BatchManagementStartupTests
{
    private IIntentStorage _intentStorage;
    private IClientTransport _clientTransport;
    private ISafetyService _safetyService;

    private const string WalletId = "w1";

    [SetUp]
    public void SetUp()
    {
        _intentStorage = Substitute.For<IIntentStorage>();
        _clientTransport = Substitute.For<IClientTransport>();
        _safetyService = Substitute.For<ISafetyService>();

        _safetyService.LockKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CompositeDisposable([], [])));

        // A stream that stays open until cancelled, as the real one does. Left unstubbed it is null,
        // the read throws, and the service's retry loop spins forever instead of ending on dispose.
        _clientTransport.GetEventStreamAsync(Arg.Any<GetEventStreamRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => OpenUntilCancelled(ci.ArgAt<CancellationToken>(1)));
    }

    [Test]
    public async Task AnOrphanedBatchInProgressIntent_IsDeletedFromTheServerOnStartup()
    {
        var orphan = CreateIntent(ArkIntentState.BatchInProgress, "server-intent-1", new OutPoint(uint256.One, 0));
        SetUpIntents(orphan);

        await StartAndStopAsync();

        await _clientTransport.Received(1).DeleteIntent(
            Arg.Is<ArkIntent>(i => i.IntentTxId == orphan.IntentTxId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ADuplicateIntentOverTheSameVtxo_IsDeletedFromTheServerToo()
    {
        // The duplicate cleanup exists to undo exactly the state arkd chokes on, so leaving the
        // server's copy behind defeats its purpose: storage looks clean while the round still fails.
        var shared = new OutPoint(uint256.One, 0);
        var newer = CreateIntent(ArkIntentState.WaitingForBatch, "server-intent-new", shared);
        var older = CreateIntent(ArkIntentState.WaitingForBatch, "server-intent-old", shared,
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        SetUpIntents(newer, older);

        await StartAndStopAsync();

        await _clientTransport.Received(1).DeleteIntent(
            Arg.Is<ArkIntent>(i => i.IntentTxId == older.IntentTxId),
            Arg.Any<CancellationToken>());
        await _clientTransport.DidNotReceive().DeleteIntent(
            Arg.Is<ArkIntent>(i => i.IntentTxId == newer.IntentTxId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnIntentWhoseBatchCommitted_IsLeftRegistered()
    {
        var committed = CreateIntent(ArkIntentState.BatchInProgress, "server-intent-2", new OutPoint(uint256.One, 0))
            with { CommitmentTransactionId = uint256.One.ToString() };
        SetUpIntents(committed);

        await StartAndStopAsync();

        await _clientTransport.DidNotReceive().DeleteIntent(
            Arg.Any<ArkIntent>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<BatchEvent> OpenUntilCancelled(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        catch (OperationCanceledException) { }
        yield break;
    }

    private async Task StartAndStopAsync()
    {
        await using var service = new BatchManagementService(
            _intentStorage,
            _clientTransport,
            Substitute.For<IVtxoStorage>(),
            Substitute.For<IContractStorage>(),
            Substitute.For<IWalletProvider>(),
            Substitute.For<ICoinService>(),
            _safetyService,
            Array.Empty<IEventHandler<PostBatchSessionEvent>>());

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
    }

    private void SetUpIntents(params ArkIntent[] intents)
    {
        _intentStorage.GetIntents(
                walletIds: Arg.Any<string[]?>(),
                intentTxIds: Arg.Any<string[]?>(),
                intentIds: Arg.Any<string[]?>(),
                containingInputs: Arg.Any<OutPoint[]?>(),
                states: Arg.Any<ArkIntentState[]?>(),
                validAt: Arg.Any<DateTimeOffset?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkIntent>>(intents));
    }

    private static ArkIntent CreateIntent(
        ArkIntentState state, string intentId, OutPoint vtxo, DateTimeOffset? updatedAt = null) =>
        new(
            IntentTxId: $"intent-{intentId}",
            IntentId: intentId,
            WalletId: WalletId,
            State: state,
            ValidFrom: DateTimeOffset.UtcNow.AddHours(-1),
            ValidUntil: DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt: DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt: updatedAt ?? DateTimeOffset.UtcNow,
            RegisterProof: "dummy",
            RegisterProofMessage: "dummy",
            DeleteProof: "dummy",
            DeleteProofMessage: "dummy",
            BatchId: null,
            CommitmentTransactionId: null,
            CancellationReason: null,
            IntentVtxos: [vtxo],
            SignerDescriptor: "dummy-signer");
}
