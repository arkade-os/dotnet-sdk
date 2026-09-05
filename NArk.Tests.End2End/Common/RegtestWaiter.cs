namespace NArk.Tests.End2End.Common;

/// <summary>
/// Waiting that needs the regtest chain to move. Conditions that do not are handled by
/// <see cref="NArk.Tests.Common.TestWaiter"/>, shared with the unit tests.
/// </summary>
internal static class RegtestWaiter
{
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
