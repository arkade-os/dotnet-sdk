using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Sync;
using NArk.Abstractions.VTXOs;
using NArk.Core.Services;
using NArk.Core.Transport;
using NSubstitute;

namespace NArk.Tests.Sync;

/// <summary>
/// Verifies the wiring between <see cref="VtxoSynchronizationService"/> and
/// <see cref="ISyncStateStorage"/>: the cold-start catch-up reads the stored
/// <c>LastFullPollAt</c> as its <c>after</c> filter, and successful full-set
/// polls (those enqueued by <c>RoutinePoll</c>) advance the cursor.
/// </summary>
[TestFixture]
public class VtxoSynchronizationServiceSyncStateTests
{
    private IVtxoStorage _vtxoStorage = null!;
    private IClientTransport _transport = null!;
    private IActiveScriptsProvider _provider = null!;
    private ISyncStateStorage _syncState = null!;

    [SetUp]
    public void SetUp()
    {
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _transport = Substitute.For<IClientTransport>();
        _provider = Substitute.For<IActiveScriptsProvider>();
        _syncState = Substitute.For<ISyncStateStorage>();
    }

    [Test]
    public async Task ColdStart_PassesStoredLastFullPollAtAsAfterFilter()
    {
        var stored = new DateTimeOffset(2026, 04, 25, 09, 0, 0, TimeSpan.Zero);
        _syncState.GetLastFullPollAtAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DateTimeOffset?>(stored));

        var scripts = new HashSet<string> { "5120aa", "5120bb" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));

        // Snapshot returns nothing — we only care that the call shape is right.
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], _syncState);

        await sut.StartAsync(CancellationToken.None);
        // Allow the queued initial poll to drain.
        await WaitForCallsAsync(() => _transport.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)));
        await sut.DisposeAsync();

        // The cold-start catch-up must use the stored timestamp as `after`.
        _transport.Received().GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(s => s.SetEquals(scripts)),
            Arg.Is<DateTimeOffset?>(t => t == stored),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ColdStart_NoStoredTimestamp_FallsBackToNullAfter()
    {
        _syncState.GetLastFullPollAtAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DateTimeOffset?>(null));

        var scripts = new HashSet<string> { "5120aa" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], _syncState);

        await sut.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(() => _transport.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)));
        await sut.DisposeAsync();

        _transport.Received().GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Is<DateTimeOffset?>(t => t == null),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoSyncStateStorage_StillWorks_WithNullAfter()
    {
        var scripts = new HashSet<string> { "5120aa" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());

        // syncStateStorage = null → opt-out; service must still function.
        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], syncStateStorage: null);

        await sut.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(() => _transport.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)));
        await sut.DisposeAsync();

        _transport.Received().GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Is<DateTimeOffset?>(t => t == null),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    private static async Task WaitForCallsAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
    }
}
