using System.Collections.Immutable;

namespace NArk.Abstractions.Safety;

public interface ISafetyService
{
    Task<bool> TryLockByTimeAsync(string key, TimeSpan timeSpan);
    Task<CompositeDisposable> LockKeyAsync(string key, CancellationToken ct);
    Task<CompositeDisposable> LockKeysAsync(ImmutableSortedSet<string> keys, CancellationToken ct);
}