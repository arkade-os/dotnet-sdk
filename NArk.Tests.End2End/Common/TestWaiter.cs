namespace NArk.Tests.End2End.Common;

/// <summary>
/// Polling helpers that replace ad-hoc <c>Task.Delay</c> / deadline-loop
/// combinations scattered across E2E tests.
/// </summary>
internal static class TestWaiter
{
    /// <summary>
    /// Polls <paramref name="predicate"/> every <paramref name="pollInterval"/> until it
    /// returns <c>true</c> or <paramref name="timeout"/> elapses.
    /// The predicate is always checked once immediately, and once more after each sleep,
    /// so it is guaranteed to run at the deadline boundary.
    /// Throws <see cref="TimeoutException"/> when the deadline is exceeded.
    /// </summary>
    internal static async Task WaitFor(
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
    internal static Task WaitFor(
        Func<bool> predicate,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
        => WaitFor(() => Task.FromResult(predicate()), timeout, pollInterval, ct);

    /// <summary>
    /// Waits for <paramref name="task"/> to complete, mining regtest blocks every
    /// <paramref name="mineInterval"/> while waiting. Useful for swap / batch tests
    /// that need on-chain progression to advance.
    /// Throws <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses.
    /// Any exception carried by <paramref name="task"/> is re-thrown after it completes.
    /// </summary>
    internal static async Task WaitForWithMining(
        Task task,
        TimeSpan timeout,
        int blocksPerTick = 1,
        TimeSpan? mineInterval = null,
        CancellationToken ct = default)
    {
        var interval = mineInterval ?? TimeSpan.FromSeconds(3);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (!task.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"Task did not complete within {timeout.TotalSeconds:0}s");

            await DockerHelper.MineBlocks(blocksPerTick, ct);
            await Task.WhenAny(task, Task.Delay(interval, ct));
        }

        // Propagate any exception the task carries.
        await task;
    }
}
