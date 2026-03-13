using System.Collections.Concurrent;
using System.Collections.Immutable;
using NArk.Abstractions.Safety;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// In-browser safety service using in-memory locks.
/// WASM is single-threaded, so locking is lightweight.
/// </summary>
public class WasmSafetyService : ISafetyService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _timeLocks = new();

    public Task<bool> TryLockByTimeAsync(string key, TimeSpan timeSpan)
    {
        var now = DateTimeOffset.UtcNow;
        if (_timeLocks.TryGetValue(key, out var expiry) && now < expiry)
            return Task.FromResult(false);
        _timeLocks[key] = now + timeSpan;
        return Task.FromResult(true);
    }

    public async Task<CompositeDisposable> LockKeyAsync(string key, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new CompositeDisposable([new SemaphoreReleaser(sem)], Array.Empty<IAsyncDisposable>());
    }

    public async Task<CompositeDisposable> LockKeysAsync(ImmutableSortedSet<string> keys, CancellationToken ct)
    {
        var sems = keys.Select(k => _locks.GetOrAdd(k, _ => new SemaphoreSlim(1, 1))).ToList();
        foreach (var sem in sems)
            await sem.WaitAsync(ct);
        return new CompositeDisposable(
            sems.Select(s => (IDisposable)new SemaphoreReleaser(s)).ToArray(),
            Array.Empty<IAsyncDisposable>());
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim sem) : IDisposable
    {
        public void Dispose() => sem.Release();
    }
}
