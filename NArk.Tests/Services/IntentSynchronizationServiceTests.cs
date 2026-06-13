using NBitcoin;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Core.Services;
using NArk.Core.Transport;
using NSubstitute;

namespace NArk.Tests.Services;

[TestFixture]
public class IntentSynchronizationServiceTests
{
    /// <summary>
    /// Reproduces the DI-scope disposal race: mock GetIntents signals that the loop
    /// is parked inside the query, waits until the shutdown token fires, then throws
    /// ObjectDisposedException (as EF does when its DbContext is disposed by the DI scope).
    /// Before the fix, this ObjectDisposedException escapes both DoIntentSubmitLoop's
    /// catch and DisposeAsync's catch (both only catch OperationCanceledException), causing
    /// DisposeAsync to rethrow — which flakes E2E tests at teardown.
    /// </summary>
    [Test, Timeout(10_000)]
    public async Task DisposeAsync_does_not_throw_when_in_flight_query_races_scope_disposal()
    {
        var intentStorage = Substitute.For<IIntentStorage>();
        var clientTransport = Substitute.For<IClientTransport>();
        var safetyService = Substitute.For<ISafetyService>();

        var inQueryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownRequestedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // When GetIntents is called, signal we're in the query, wait for shutdown to be
        // requested (simulates the EF cancellation race), then throw ObjectDisposedException.
        intentStorage.GetIntents(
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
            .Returns(async _ =>
            {
                inQueryTcs.TrySetResult();
                await shutdownRequestedTcs.Task;
                throw new ObjectDisposedException("IServiceProvider");
                return (IReadOnlyCollection<ArkIntent>)[]; // unreachable, satisfies return type
            });

        var sut = new IntentSynchronizationService(intentStorage, clientTransport, safetyService);
        await sut.StartAsync();

        // Wait until the background loop is confirmed inside GetIntents.
        await inQueryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Now signal that "scope disposal" happened and unblock the mock.
        shutdownRequestedTcs.TrySetResult();

        // DisposeAsync must complete without throwing, even though the in-flight
        // GetIntents task throws ObjectDisposedException after the shutdown token fires.
        Assert.DoesNotThrowAsync(async () => await sut.DisposeAsync());
    }
}
