namespace NArk.Tests.Common;

/// <summary>
/// Polling helpers that replace ad-hoc <c>Task.Delay</c> / deadline-loop combinations in
/// tests that drive background work. Lives here rather than in the E2E project so unit
/// tests can use it too.
/// </summary>
public static class TestWaiter
{
    /// <summary>
    /// Polls <paramref name="predicate"/> every <paramref name="pollInterval"/> until it
    /// returns <c>true</c> or <paramref name="timeout"/> elapses.
    /// The predicate is always checked once immediately, and once more after each sleep,
    /// so it is guaranteed to run at the deadline boundary.
    /// Throws <see cref="TimeoutException"/> when the deadline is exceeded.
    /// </summary>
    public static async Task WaitFor(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await predicate()) return;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:0}s");

            await Task.Delay(remaining < interval ? remaining : interval, ct);
        }
    }

    /// <summary>
    /// Synchronous-predicate overload.
    /// </summary>
    public static Task WaitFor(
        Func<bool> predicate,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
        => WaitFor(() => Task.FromResult(predicate()), timeout, pollInterval, ct);

    /// <summary>
    /// As <see cref="WaitFor(Func{bool}, TimeSpan, TimeSpan?, CancellationToken)"/>, but
    /// returns instead of throwing when the deadline passes. Use it before an assertion that
    /// reports the failure better than a <see cref="TimeoutException"/> would — a mock's
    /// "expected this call, received these" beats "condition not met within 10s".
    /// </summary>
    public static async Task TryWaitFor(Func<bool> predicate, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(interval);
        }
    }
}
