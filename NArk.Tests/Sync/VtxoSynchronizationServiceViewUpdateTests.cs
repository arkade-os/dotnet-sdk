using System.Runtime.CompilerServices;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Services;
using NArk.Core.Transport;
using NSubstitute;

namespace NArk.Tests.Sync;

/// <summary>
/// Covers the in-place subscription model: a script-set change updates arkd's subscription
/// via Subscribe/Unsubscribe without tearing the stream down, the subscription is recreated
/// when arkd GC's it, an empty set tears it down, and the fresh-derive safety-net poll still
/// reconciles a drifted/missed subscription.
/// </summary>
[TestFixture]
public class VtxoSynchronizationServiceViewUpdateTests
{
    private const string ScriptA = "5120aa";
    private const string ScriptB = "5120bb";
    private const string ScriptC = "5120cc";

    private IVtxoStorage _vtxoStorage = null!;
    private IClientTransport _transport = null!;
    private int _subCounter;

    [SetUp]
    public void SetUp()
    {
        _subCounter = 0;
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _transport = Substitute.For<IClientTransport>();
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());
        // Create (subscriptionId == null) ⇒ a fresh id; add-to-existing ⇒ echo the id back.
        _transport.SubscribeForScriptsAsync(Arg.Any<IReadOnlySet<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.ArgAt<string?>(1) ?? $"sub-{Interlocked.Increment(ref _subCounter)}"));
        // Keep the stream open until cancelled, so an in-place update can't be mistaken for a restart.
        _transport.GetVtxoSubscriptionStreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => BlockingStream(ci.ArgAt<CancellationToken>(1)));
    }

    [Test]
    public async Task InitialActiveSet_CreatesSubscriptionAndOpensStream()
    {
        var provider = MutableProvider(ScriptA, ScriptB);
        await using var sut = New([provider]);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => StreamOpenCount() >= 1);

        await _transport.Received().SubscribeForScriptsAsync(
            Arg.Is<IReadOnlySet<string>>(s => s.SetEquals(new HashSet<string> { ScriptA, ScriptB })),
            Arg.Is<string?>(id => id == null), Arg.Any<CancellationToken>());
        Assert.That(StreamOpenCount(), Is.EqualTo(1));
    }

    [Test]
    public async Task ScriptAdded_UpdatesInPlace_WithoutRestartingStream()
    {
        var scripts = new HashSet<string> { ScriptA };
        var provider = MutableProvider(scripts);
        await using var sut = New([provider]);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => StreamOpenCount() >= 1);

        // A new contract is derived → fires ActiveScriptsChanged.
        scripts.Add(ScriptC);
        provider.ActiveScriptsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);

        await WaitForAsync(() => CallCount(nameof(IClientTransport.SubscribeForScriptsAsync)) >= 2);

        // The new script was added to the EXISTING subscription (non-null id), not a fresh one.
        await _transport.Received().SubscribeForScriptsAsync(
            Arg.Is<IReadOnlySet<string>>(s => s.SetEquals(new HashSet<string> { ScriptC })),
            Arg.Is<string?>(id => id != null), Arg.Any<CancellationToken>());
        // And the stream was never reopened — the watched set changed in place.
        Assert.That(StreamOpenCount(), Is.EqualTo(1), "in-place update must not restart the stream");
    }

    [Test]
    public async Task ScriptRemoved_UnsubscribesInPlace()
    {
        var scripts = new HashSet<string> { ScriptA, ScriptB };
        var provider = MutableProvider(scripts);
        await using var sut = New([provider]);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => StreamOpenCount() >= 1);

        scripts.Remove(ScriptB);
        provider.ActiveScriptsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);

        await WaitForAsync(() => CallCount(nameof(IClientTransport.UnsubscribeForScriptsAsync)) >= 1);

        await _transport.Received().UnsubscribeForScriptsAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlySet<string>?>(s => s != null && s.SetEquals(new HashSet<string> { ScriptB })),
            Arg.Any<CancellationToken>());
        Assert.That(StreamOpenCount(), Is.EqualTo(1));
    }

    [Test]
    public async Task EmptyActiveSet_TearsSubscriptionDown()
    {
        var scripts = new HashSet<string> { ScriptA };
        var provider = MutableProvider(scripts);
        await using var sut = New([provider]);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => StreamOpenCount() >= 1);

        scripts.Clear();
        provider.ActiveScriptsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);

        await WaitForAsync(() => CallCount(nameof(IClientTransport.UnsubscribeForScriptsAsync)) >= 1);

        // Unsubscribe-all (null scripts) tears the subscription down server-side.
        await _transport.Received().UnsubscribeForScriptsAsync(
            Arg.Any<string>(), Arg.Is<IReadOnlySet<string>?>(s => s == null), Arg.Any<CancellationToken>());
        Assert.That(sut.ListenedScripts, Is.Empty);
    }

    [Test]
    public async Task SubscriptionGarbageCollected_Recreates()
    {
        var scripts = new HashSet<string> { ScriptA };
        var provider = MutableProvider(scripts);

        // The in-place add (non-null subscription id) fails as if arkd GC'd the listener;
        // the recreate (null id) succeeds.
        _transport.SubscribeForScriptsAsync(Arg.Any<IReadOnlySet<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<string?>(1) is null
                ? Task.FromResult($"sub-{Interlocked.Increment(ref _subCounter)}")
                : Task.FromException<string>(new InvalidOperationException("subscription sub-1 not found")));

        await using var sut = New([provider]);
        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => StreamOpenCount() >= 1);

        scripts.Add(ScriptC);
        provider.ActiveScriptsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);

        // Recreate ⇒ a second create call (null id) and a second stream open on the new id.
        await WaitForAsync(() => CreateCount() >= 2 && StreamOpenCount() >= 2);

        Assert.That(CreateCount(), Is.GreaterThanOrEqualTo(2), "GC'd subscription must be recreated with a fresh id");
        Assert.That(sut.ListenedScripts, Does.Contain(ScriptC));
    }

    [Test]
    public async Task StaleSubscription_SelfHealsViaRoutinePoll()
    {
        var vtxoProvider = MutableProvider(ScriptA, ScriptB);
        var contractScripts = new HashSet<string>();
        var contractProvider = MutableProvider(contractScripts);

        await using var sut = New([vtxoProvider, contractProvider], TimeSpan.FromMilliseconds(100));
        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => sut.ListenedScripts.Count > 0);

        // Contract becomes active WITHOUT raising the event — only the safety-net poll can find it.
        contractScripts.Add(ScriptC);

        await WaitForAsync(() => PolledScripts().Contains(ScriptC));
        await WaitForAsync(() => sut.ListenedScripts.Contains(ScriptC));

        Assert.That(PolledScripts(), Does.Contain(ScriptC));
        Assert.That(sut.ListenedScripts, Does.Contain(ScriptC));
    }

    [Test]
    public async Task OneProviderThrows_OtherProvidersStillPolled()
    {
        var healthy = MutableProvider(ScriptA);
        var faulty = Substitute.For<IActiveScriptsProvider>();
        faulty.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns<Task<HashSet<string>>>(_ => throw new InvalidOperationException("provider down"));

        await using var sut = New([healthy, faulty], TimeSpan.FromMilliseconds(100));
        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => PolledScripts().Contains(ScriptA));

        Assert.That(sut.ListenedScripts, Does.Contain(ScriptA),
            "a failing provider must be skipped, not blank the set");
    }

    private VtxoSynchronizationService New(IActiveScriptsProvider[] providers, TimeSpan? pollInterval = null) =>
        new(_vtxoStorage, _transport, providers, walletStorage: null)
        {
            // Default to a long interval so event-driven tests aren't perturbed by the poll.
            RoutinePollInterval = pollInterval ?? TimeSpan.FromSeconds(30)
        };

    private static IActiveScriptsProvider MutableProvider(params string[] scripts)
        => MutableProvider(new HashSet<string>(scripts));

    private static IActiveScriptsProvider MutableProvider(HashSet<string> backing)
    {
        var provider = Substitute.For<IActiveScriptsProvider>();
        provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new HashSet<string>(backing)));
        return provider;
    }

    private int CallCount(string method) =>
        _transport.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);

    private int StreamOpenCount() => CallCount(nameof(IClientTransport.GetVtxoSubscriptionStreamAsync));

    // Number of "create" calls = SubscribeForScriptsAsync with a null subscription id.
    private int CreateCount() =>
        _transport.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IClientTransport.SubscribeForScriptsAsync))
            .Count(c => c.GetArguments()[1] is null);

    private HashSet<string> PolledScripts()
    {
        var polled = new HashSet<string>();
        foreach (var call in _transport.ReceivedCalls()
                     .Where(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)))
        {
            if (call.GetArguments()[0] is IReadOnlySet<string> scripts)
                polled.UnionWith(scripts);
        }
        return polled;
    }

    private static async IAsyncEnumerable<HashSet<string>> BlockingStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource();
        await using (ct.Register(() => tcs.TrySetResult()))
        {
            await tcs.Task;
        }
        yield break;
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
    }
}
