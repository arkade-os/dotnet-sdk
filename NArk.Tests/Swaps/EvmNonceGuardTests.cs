using NArk.Swaps.Evm;
using NUnit.Framework;

namespace NArk.Tests.Swaps;

/// <summary>
/// Unit tests for <see cref="EvmNonceGuard"/> — the mutual exclusion that stops two concurrent
/// swaps from resolving and signing the same account nonce.
/// </summary>
/// <remarks>
/// The property under test is that broadcasts never overlap. Nethereum resolves the nonce inside
/// the send call, so any overlap means two transactions can be signed against the same pending
/// count — and the node then drops or replaces one of them. Since a broadcast is how funds get
/// committed, the loser is a lock that never happened or a refund that never landed.
/// </remarks>
[TestFixture]
public class EvmNonceGuardTests
{
    [Test]
    public async Task ConcurrentBroadcasts_NeverOverlap()
    {
        using var guard = new EvmNonceGuard();
        var inFlight = 0;
        var maxObserved = 0;
        var completed = 0;

        async Task<string> Broadcast(int i)
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxObserved, now);
            await Task.Delay(10);           // window in which an unguarded caller would overlap
            Interlocked.Decrement(ref inFlight);
            Interlocked.Increment(ref completed);
            return $"0x{i:x}";
        }

        var hashes = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => guard.BroadcastAsync(() => Broadcast(i))));

        Assert.Multiple(() =>
        {
            Assert.That(maxObserved, Is.EqualTo(1), "Broadcasts must be strictly serialised");
            Assert.That(completed, Is.EqualTo(20));
            Assert.That(hashes, Is.Unique);
        });
    }

    /// <summary>
    /// A failed broadcast must release the gate — otherwise the first reverted transaction
    /// deadlocks every later swap in the process.
    /// </summary>
    [Test]
    public async Task FailedBroadcast_ReleasesTheGate()
    {
        using var guard = new EvmNonceGuard();

        Assert.That(
            async () => await guard.BroadcastAsync(() => throw new InvalidOperationException("reverted")),
            Throws.InvalidOperationException);

        var after = await guard.BroadcastAsync(() => Task.FromResult("0xok"));
        Assert.That(after, Is.EqualTo("0xok"));
    }

    /// <summary>Cancellation while queued must not consume a permit.</summary>
    [Test]
    public async Task CancelledWaiter_DoesNotLeakThePermit()
    {
        using var guard = new EvmNonceGuard();
        using var cts = new CancellationTokenSource();

        var release = new TaskCompletionSource();
        var holder = guard.BroadcastAsync(async () => { await release.Task; return "0xheld"; });

        var queued = guard.BroadcastAsync(() => Task.FromResult("0xnever"), cts.Token);
        await cts.CancelAsync();
        Assert.That(async () => await queued, Throws.InstanceOf<OperationCanceledException>());

        release.SetResult();
        Assert.That(await holder, Is.EqualTo("0xheld"));
        Assert.That(await guard.BroadcastAsync(() => Task.FromResult("0xafter")), Is.EqualTo("0xafter"));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }
}
