using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class IntentSynchronizationServiceTests
{
    private IIntentStorage _intentStorage;
    private IClientTransport _clientTransport;
    private ISafetyService _safetyService;

    [SetUp]
    public void SetUp()
    {
        _intentStorage = Substitute.For<IIntentStorage>();
        _clientTransport = Substitute.For<IClientTransport>();
        _safetyService = Substitute.For<ISafetyService>();

        // Default: safety service returns a no-op disposable
        _safetyService.LockKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CompositeDisposable([], [])));
    }

    [Test]
    public async Task ExpiredIntentIsMarkedAsCancelled()
    {
        // Arrange: Create an intent that has already expired
        var expiredIntent = CreateIntent(
            intentTxId: "expired-intent-tx-id",
            state: ArkIntentState.WaitingToSubmit,
            validFrom: DateTimeOffset.UtcNow.AddHours(-2),
            validUntil: DateTimeOffset.UtcNow.AddHours(-1) // Expired 1 hour ago
        );

        _intentStorage.GetIntents(
                walletIds: Arg.Any<string[]?>(),
                intentTxIds: Arg.Any<string[]?>(),
                intentIds: Arg.Any<string[]?>(),
                containingInputs: Arg.Any<OutPoint[]?>(),
                states: Arg.Is<ArkIntentState[]?>(s => s != null && s.Contains(ArkIntentState.WaitingToSubmit)),
                validAt: Arg.Any<DateTimeOffset?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkIntent>>([expiredIntent]));

        ArkIntent? savedIntent = null;
        _intentStorage.SaveIntent(Arg.Any<string>(), Arg.Any<ArkIntent>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                savedIntent = call.Arg<ArkIntent>();
                return Task.CompletedTask;
            });

        await using var service = new IntentSynchronizationService(
            _intentStorage, _clientTransport, _safetyService);

        // Act: Start the service and let it process
        await service.StartAsync(CancellationToken.None);

        // Give it a moment to process
        await Task.Delay(100);

        // Assert: The intent should be marked as cancelled
        await _intentStorage.Received(1).SaveIntent(
            expiredIntent.WalletId,
            Arg.Is<ArkIntent>(i =>
                i.IntentTxId == expiredIntent.IntentTxId &&
                i.State == ArkIntentState.Cancelled &&
                i.CancellationReason == "Intent expired"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NotYetValidIntentIsNotSubmitted()
    {
        // Arrange: Create an intent that is not yet valid
        var futureIntent = CreateIntent(
            intentTxId: "future-intent-tx-id",
            state: ArkIntentState.WaitingToSubmit,
            validFrom: DateTimeOffset.UtcNow.AddHours(1), // Valid in 1 hour
            validUntil: DateTimeOffset.UtcNow.AddHours(2)
        );

        _intentStorage.GetIntents(
                walletIds: Arg.Any<string[]?>(),
                intentTxIds: Arg.Any<string[]?>(),
                intentIds: Arg.Any<string[]?>(),
                containingInputs: Arg.Any<OutPoint[]?>(),
                states: Arg.Is<ArkIntentState[]?>(s => s != null && s.Contains(ArkIntentState.WaitingToSubmit)),
                validAt: Arg.Any<DateTimeOffset?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkIntent>>([futureIntent]));

        await using var service = new IntentSynchronizationService(
            _intentStorage, _clientTransport, _safetyService);

        // Act: Start the service and let it process
        await service.StartAsync(CancellationToken.None);

        // Give it a moment to process
        await Task.Delay(100);

        // Assert: The intent should NOT be saved (not cancelled, not submitted)
        await _intentStorage.DidNotReceive().SaveIntent(
            Arg.Any<string>(),
            Arg.Any<ArkIntent>(),
            Arg.Any<CancellationToken>());

        // Assert: RegisterIntent should NOT have been called
        await _clientTransport.DidNotReceive().RegisterIntent(
            Arg.Any<ArkIntent>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidIntentIsSubmitted()
    {
        // Arrange: Create a valid intent
        var validIntent = CreateIntent(
            intentTxId: "valid-intent-tx-id",
            state: ArkIntentState.WaitingToSubmit,
            validFrom: DateTimeOffset.UtcNow.AddHours(-1), // Valid since 1 hour ago
            validUntil: DateTimeOffset.UtcNow.AddHours(1)  // Valid for 1 more hour
        );

        _intentStorage.GetIntents(
                walletIds: Arg.Any<string[]?>(),
                intentTxIds: Arg.Any<string[]?>(),
                intentIds: Arg.Any<string[]?>(),
                containingInputs: Arg.Any<OutPoint[]?>(),
                states: Arg.Is<ArkIntentState[]?>(s => s != null && s.Contains(ArkIntentState.WaitingToSubmit)),
                validAt: Arg.Any<DateTimeOffset?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkIntent>>([validIntent]));

        _intentStorage.GetIntents(
                walletIds: Arg.Any<string[]?>(),
                intentTxIds: Arg.Is<string[]?>(ids => ids != null && ids.Contains(validIntent.IntentTxId)),
                intentIds: Arg.Any<string[]?>(),
                containingInputs: Arg.Any<OutPoint[]?>(),
                states: Arg.Any<ArkIntentState[]?>(),
                validAt: Arg.Any<DateTimeOffset?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkIntent>>([validIntent]));

        _clientTransport.RegisterIntent(Arg.Any<ArkIntent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("server-intent-id-123"));

        await using var service = new IntentSynchronizationService(
            _intentStorage, _clientTransport, _safetyService);

        // Act: Start the service and let it process
        await service.StartAsync(CancellationToken.None);

        // Give it a moment to process
        await Task.Delay(100);

        // Assert: RegisterIntent should have been called
        await _clientTransport.Received(1).RegisterIntent(
            Arg.Is<ArkIntent>(i => i.IntentTxId == validIntent.IntentTxId),
            Arg.Any<CancellationToken>());

        // Assert: Intent should be saved with WaitingForBatch state and IntentId
        await _intentStorage.Received(1).SaveIntent(
            validIntent.WalletId,
            Arg.Is<ArkIntent>(i =>
                i.IntentTxId == validIntent.IntentTxId &&
                i.State == ArkIntentState.WaitingForBatch &&
                i.IntentId == "server-intent-id-123"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MultipleIntentsAreProcessedCorrectly()
    {
        // Arrange: Create mixed intents - one expired, one not yet valid, one valid
        var expiredIntent = CreateIntent(
            intentTxId: "expired-intent",
            state: ArkIntentState.WaitingToSubmit,
            validFrom: DateTimeOffset.UtcNow.AddHours(-2),
            validUntil: DateTimeOffset.UtcNow.AddHours(-1)
        );

        var futureIntent = CreateIntent(
            intentTxId: "future-intent",
            state: ArkIntentState.WaitingToSubmit,
            validFrom: DateTimeOffset.UtcNow.AddHours(1),
            validUntil: DateTimeOffset.UtcNow.AddHours(2)
        );

        var validIntent = CreateIntent(
            intentTxId: "valid-intent",
            state: ArkIntentState.WaitingToSubmit,
            validFrom: DateTimeOffset.UtcNow.AddHours(-1),
            validUntil: DateTimeOffset.UtcNow.AddHours(1)
        );

        _intentStorage.GetIntents(
                walletIds: Arg.Any<string[]?>(),
                intentTxIds: Arg.Any<string[]?>(),
                intentIds: Arg.Any<string[]?>(),
                containingInputs: Arg.Any<OutPoint[]?>(),
                states: Arg.Is<ArkIntentState[]?>(s => s != null && s.Contains(ArkIntentState.WaitingToSubmit)),
                validAt: Arg.Any<DateTimeOffset?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkIntent>>([expiredIntent, futureIntent, validIntent]));

        _intentStorage.GetIntents(
                walletIds: Arg.Any<string[]?>(),
                intentTxIds: Arg.Is<string[]?>(ids => ids != null && ids.Contains(validIntent.IntentTxId)),
                intentIds: Arg.Any<string[]?>(),
                containingInputs: Arg.Any<OutPoint[]?>(),
                states: Arg.Any<ArkIntentState[]?>(),
                validAt: Arg.Any<DateTimeOffset?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkIntent>>([validIntent]));

        _clientTransport.RegisterIntent(Arg.Any<ArkIntent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("server-intent-id"));

        await using var service = new IntentSynchronizationService(
            _intentStorage, _clientTransport, _safetyService);

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Assert: Expired intent should be cancelled
        await _intentStorage.Received(1).SaveIntent(
            expiredIntent.WalletId,
            Arg.Is<ArkIntent>(i =>
                i.IntentTxId == expiredIntent.IntentTxId &&
                i.State == ArkIntentState.Cancelled &&
                i.CancellationReason == "Intent expired"),
            Arg.Any<CancellationToken>());

        // Assert: Valid intent should be submitted
        await _clientTransport.Received(1).RegisterIntent(
            Arg.Is<ArkIntent>(i => i.IntentTxId == validIntent.IntentTxId),
            Arg.Any<CancellationToken>());

        // Assert: Future intent should NOT trigger any action (not cancelled, not submitted)
        await _clientTransport.DidNotReceive().RegisterIntent(
            Arg.Is<ArkIntent>(i => i.IntentTxId == futureIntent.IntentTxId),
            Arg.Any<CancellationToken>());
    }

    private static ArkIntent CreateIntent(
        string intentTxId,
        ArkIntentState state,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string? intentId = null,
        string walletId = "test-wallet")
    {
        return new ArkIntent(
            IntentTxId: intentTxId,
            IntentId: intentId,
            WalletId: walletId,
            State: state,
            ValidFrom: validFrom,
            ValidUntil: validUntil,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            RegisterProof: "dummy-register-proof",
            RegisterProofMessage: "dummy-message",
            DeleteProof: "dummy-delete-proof",
            DeleteProofMessage: "dummy-delete-message",
            BatchId: null,
            CommitmentTransactionId: null,
            CancellationReason: null,
            IntentVtxos: [],
            SignerDescriptor: "dummy-signer"
        );
    }
}
