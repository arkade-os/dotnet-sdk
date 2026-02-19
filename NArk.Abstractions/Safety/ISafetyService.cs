using System.Collections.Immutable;

namespace NArk.Abstractions.Safety;

/// <summary>
/// Provides distributed locking to prevent concurrent operations on the same resources
/// (e.g., double-spending the same VTXO, concurrent batch signing for the same wallet).
/// </summary>
public interface ISafetyService
{
    /// <summary>
    /// Attempts to acquire a time-limited lock. Returns true if acquired, false if already held.
    /// </summary>
    Task<bool> TryLockByTimeAsync(string key, TimeSpan timeSpan);
    /// <summary>
    /// Acquires an exclusive lock on a single key. Dispose the result to release.
    /// </summary>
    Task<CompositeDisposable> LockKeyAsync(string key, CancellationToken ct);
    /// <summary>
    /// Acquires exclusive locks on multiple keys atomically (sorted to prevent deadlocks).
    /// Dispose the result to release all locks.
    /// </summary>
    Task<CompositeDisposable> LockKeysAsync(ImmutableSortedSet<string> keys, CancellationToken ct);
}