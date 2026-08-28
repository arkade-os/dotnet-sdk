namespace NArk.Tests.Common;

/// <summary>
/// Waits for a condition instead of sleeping for a fixed span. Tests that drive a
/// background service race it, and a sleep long enough to be safe on a loaded CI runner
/// is far longer than the wait usually needs.
/// </summary>
public static class TestWaiter
{
    /// <summary>
    /// Polls <paramref name="predicate"/> until it holds or <paramref name="timeoutMs"/>
    /// elapses. Returns either way: the assertion that follows is what reports the failure,
    /// with a message about what was actually missing.
    /// </summary>
    public static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }
    }
}
