using System.Runtime.CompilerServices;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Services;
using NArk.Core.Transport;
using NSubstitute;

namespace NArk.Tests.Sync;

/// <summary>
/// Covers how <see cref="VtxoSynchronizationService"/> keeps the set of polled
/// scripts in sync with reality, and reproduces the "payment to a freshly-derived
/// Active receive contract is not auto-detected" report: the safety-net poll must
/// derive the active set fresh from the providers (provider-agnostic), so a stale
/// or missed stream subscription can never hide a script from detection.
/// </summary>
[TestFixture]
public class VtxoSynchronizationServiceViewUpdateTests
{
    private const string ReceiveScript = "5120b98a01252ca661028207db2cb3475a84a67691a53c5321a78504c03a0e2e5866";

    private IVtxoStorage _vtxoStorage = null!;
    private IClientTransport _transport = null!;

    [SetUp]
    public void SetUp()
    {
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _transport = Substitute.For<IClientTransport>();
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());
        // Keep the subscription stream open until its token is cancelled, so the
        // service doesn't spin in graceful-restart while the test runs.
        _transport.GetVtxoToPollAsStream(Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => BlockingStream(ci.ArgAt<CancellationToken>(1)));
    }

    [Test]
    public async Task NewlyActiveContract_ViaEvent_StartsBeingListenedTo()
    {
        var contractScripts = new HashSet<string>();
        var contractProvider = MakeProvider(() => contractScripts);

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [contractProvider], walletStorage: null);

        await sut.StartAsync(CancellationToken.None);

        // A new Receive contract is derived → contract store reports it active and
        // raises ActiveScriptsChanged (mirrors EfCoreContractStorage.SaveContract).
        contractScripts.Add(ReceiveScript);
        contractProvider.ActiveScriptsChanged += Raise.Event<EventHandler>(contractProvider, EventArgs.Empty);

        await WaitForAsync(() => sut.ListenedScripts.Contains(ReceiveScript));
        await sut.DisposeAsync();

        Assert.That(sut.ListenedScripts, Does.Contain(ReceiveScript));
    }

    [Test]
    public async Task StaleSubscription_SelfHealsViaRoutinePoll()
    {
        // The bug: the contract is active in storage, but no ActiveScriptsChanged
        // reached the service (lost/aborted event during the rapid swap setup), so
        // the stream subscription never learned about it. The safety-net poll must
        // still discover and poll the script, because it re-derives the active set
        // fresh rather than trusting the (stale) subscription set.
        var vtxoProvider = MakeProvider(() => new HashSet<string> { "5120aa", "5120bb" });
        var contractScripts = new HashSet<string>();
        var contractProvider = MakeProvider(() => contractScripts);

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [vtxoProvider, contractProvider], walletStorage: null)
        {
            RoutinePollInterval = TimeSpan.FromMilliseconds(100)
        };

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => sut.ListenedScripts.Count > 0);

        // Contract becomes active WITHOUT raising the event.
        contractScripts.Add(ReceiveScript);

        // RoutinePoll re-derives the active set and must both poll the new script
        // and resync the subscription to it.
        await WaitForAsync(() => PolledScripts().Contains(ReceiveScript));
        await WaitForAsync(() => sut.ListenedScripts.Contains(ReceiveScript));
        await sut.DisposeAsync();

        Assert.That(PolledScripts(), Does.Contain(ReceiveScript),
            "The safety-net poll must derive the active set fresh and poll a contract the stream never subscribed to.");
        Assert.That(sut.ListenedScripts, Does.Contain(ReceiveScript),
            "RoutinePoll must resync the subscription to the freshly-discovered script.");
    }

    [Test]
    public async Task OneProviderThrows_OtherProvidersStillPolled()
    {
        var healthy = MakeProvider(() => new HashSet<string> { "5120aa" });
        var faulty = Substitute.For<IActiveScriptsProvider>();
        faulty.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns<Task<HashSet<string>>>(_ => throw new InvalidOperationException("provider down"));

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [healthy, faulty], walletStorage: null)
        {
            RoutinePollInterval = TimeSpan.FromMilliseconds(100)
        };

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => PolledScripts().Contains("5120aa"));
        await sut.DisposeAsync();

        Assert.That(sut.ListenedScripts, Does.Contain("5120aa"),
            "A failing provider must be skipped, not abort the whole refresh and blank the set.");
        Assert.That(PolledScripts(), Does.Contain("5120aa"));
    }

    private static IActiveScriptsProvider MakeProvider(Func<HashSet<string>> currentScripts)
    {
        var provider = Substitute.For<IActiveScriptsProvider>();
        provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new HashSet<string>(currentScripts())));
        return provider;
    }

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
