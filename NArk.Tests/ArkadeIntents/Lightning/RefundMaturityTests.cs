using NArk.ArkadeIntents.Lightning;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// When a refund becomes takeable.
/// </summary>
/// <remarks>
/// <para>
/// The refund leaf matures against median time past — the median of the last eleven block times —
/// not against anybody's clock. MTP trails real time, and trails it further when blocks come
/// slowly, so the two answers disagree exactly when it matters: a spend built on the wall clock's
/// word is broadcast into a chain that will not confirm it, and the failure reads like a broken
/// covenant rather than an early attempt.
/// </para>
/// <para>
/// Asked of the chain, the refund opens exactly when it opens. The fixed wait exists only for a
/// caller with no chain to ask, where being too patient is the only safe way to be wrong.
/// </para>
/// </remarks>
[TestFixture]
public class RefundMaturityTests
{
    private const long Locktime = 1_800_000_000;

    [Test]
    public void WithAChainToAsk_MaturityFollowsMedianTimePast_NotTheClock()
    {
        // MTP has passed the locktime, so the spend will confirm — even though a pessimistic
        // fixed wait would still be refusing.
        Assert.That(Reached(chainNow: Locktime + 1, wallClock: Locktime + 1), Is.True);
    }

    [Test]
    public void WithAChainToAsk_ARefundIsRefusedWhileMtpLags()
    {
        // The clock says the locktime passed an hour ago; the chain has not caught up, so a spend
        // built now cannot confirm. Believing the clock here is what produces the confusing failure.
        Assert.That(Reached(chainNow: Locktime - 600, wallClock: Locktime + 3600), Is.False);
    }

    [Test]
    public void WithNoChainToAsk_TheFixedWaitIsServed()
    {
        // No blockchain injected: fall back to the clock plus the worst-case lag. Just past the
        // locktime is not enough, because MTP may be anywhere behind it.
        Assert.That(Reached(chainNow: null, wallClock: Locktime + 60), Is.False);
    }

    [Test]
    public void WithNoChainToAsk_TheFixedWaitEventuallyExpires()
    {
        var past = Locktime + LightningSendClient.MedianTimePastLagSeconds;

        Assert.That(Reached(chainNow: null, wallClock: past), Is.True);
    }

    /// <summary>
    /// Whether a refund at <paramref name="wallClock"/> is permitted, given <paramref name="chain"/>.
    /// </summary>
    /// <remarks>
    /// Drives the real gate through the client's own private check, so the test cannot drift from
    /// the rule it is pinning — the alternative is restating the arithmetic here and asserting that
    /// it equals itself.
    /// </remarks>
    private static bool Reached(long? chainNow, long wallClock)
    {
        try
        {
            LightningSendClient.AssertLocktimeReached("swap-1", Locktime, chainNow, wallClock);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
