using System.Runtime.CompilerServices;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Services;
using NArk.Core.Transport;
using NSubstitute;

namespace NArk.Tests.Sync;

/// <summary>
/// Covers the in-place subscription model: a script-set change updates arkd's subscription
/// via UpdateSubscription without tearing the stream down, the subscription is recreated
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
        // New subscription (no existing ID) → yield SubscriptionStarted then block.
        _transport.OpenSubscriptionStreamAsync(
                Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var existingId = ci.ArgAt<string?>(1);
                var ct = ci.ArgAt<CancellationToken>(2);
                var id = existingId ?? $"sub-{Interlocked.Increment(ref _subCounter)}";
                return NewSubscriptionStream(id, ct);
            });
    }

    [Test]
    public async Task InitialActiveSet_OpensStreamAndReceivesSubscriptionId()
    {
        var provider = MutableProvider(ScriptA, ScriptB);
        await using var sut = New([provider]);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => StreamOpenCount() >= 1);

        // Stream opened with initial scripts and no existing ID.
        _transport.Received().OpenSubscriptionStreamAsync(
            Arg.Is<IReadOnlySet<string>?>(s => s != null && s.SetEquals(new HashSet<string> { ScriptA, ScriptB })),
            Arg.Is<string?>(id => id == null),
            Arg.Any<CancellationToken>());
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

        scripts.Add(ScriptC);
        provider.ActiveScriptsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);

        await WaitForAsync(() => UpdateCount() >= 1);

        // In-place update: add ScriptC on the existing subscription, do not restart stream.
        await _transport.Received().UpdateSubscriptionScriptsAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlySet<string>?>(s => s != null && s.SetEquals(new HashSet<string> { ScriptC })),
            Arg.Is<IReadOnlySet<string>?>(s => s == null || s.Count == 0),
            Arg.Any<CancellationToken>());
        Assert.That(StreamOpenCount(), Is.EqualTo(1), "in-place update must not restart the stream");
    }

    [Test]
    public async Task ScriptRemoved_UpdatesInPlace()
    {
        var scripts = new HashSet<string> { ScriptA, ScriptB };
        var provider = MutableProvider(scripts);
        await using var sut = New([provider]);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => StreamOpenCount() >= 1);

        scripts.Remove(ScriptB);
        provider.ActiveScriptsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);

        await WaitForAsync(() => UpdateCount() >= 1);

        await _transport.Received().UpdateSubscriptionScriptsAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlySet<string>?>(s => s == null || s.Count == 0),
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

        await WaitForAsync(() => sut.ListenedScripts.Count == 0, timeoutMs: 3000);

        Assert.That(sut.ListenedScripts, Is.Empty);
        // No extra stream opens after teardown.
        Assert.That(StreamOpenCount(), Is.EqualTo(1));
    }

    [Test]
    public async Task SubscriptionGarbageCollected_Recreates()
    {
        var scripts = new HashSet<string> { ScriptA };
        var provider = MutableProvider(scripts);

        // UpdateSubscription fails as if arkd GC'd the listener; the supervisor then reopens fresh.
        _transport.UpdateSubscriptionScriptsAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("subscription sub-1 not found")));

        await using var sut = New([provider]);
        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => StreamOpenCount() >= 1);

        scripts.Add(ScriptC);
        provider.ActiveScriptsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);

        // GC → UpdateSubscription throws → supervisor reopens fresh → second stream open.
        await WaitForAsync(() => StreamOpenCount() >= 2);

        Assert.That(StreamOpenCount(), Is.GreaterThanOrEqualTo(2), "GC'd subscription must cause a fresh stream open");
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

    private int StreamOpenCount() => CallCount(nameof(IClientTransport.OpenSubscriptionStreamAsync));
    private int UpdateCount() => CallCount(nameof(IClientTransport.UpdateSubscriptionScriptsAsync));

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

    private static async IAsyncEnumerable<VtxoSubscriptionEvent> NewSubscriptionStream(
        string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new VtxoSubscriptionStarted(id);
        var tcs = new TaskCompletionSource();
        await using (ct.Register(() => tcs.TrySetResult()))
            await tcs.Task;
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
