using System.Collections.Immutable;
using AsyncKeyedLock;
using NArk.Abstractions.Safety;

namespace NArk.Safety.AsyncKeyedLock;

public class AsyncSafetyService : ISafetyService
{
    private readonly AsyncKeyedLocker<string> _locker = new();
    public async Task<bool> TryLockByTimeAsync(string key, TimeSpan timeSpan)
    {
        var nullableLock = await _locker.LockOrNullAsync(key, TimeSpan.Zero, ConfigureAwaitOptions.None);
        if (nullableLock == null)
            return false;

        new CancellationTokenSource(timeSpan)
            .Token
            .Register(() => nullableLock.Dispose());
        return true;
    }

    public async Task<CompositeDisposable> LockKeyAsync(string key, CancellationToken ct) =>
        new([await _locker.LockAsync(key, ct)], []);

    public async Task<CompositeDisposable> LockKeysAsync(ImmutableSortedSet<string> keys, CancellationToken ct)
    {
        var failsafeActivated = false;
        List<IDisposable> disposables = [];

        foreach (var key in keys)
        {
            try
            {
                disposables.Add(await _locker.LockAsync(key, ct));
            }
            catch
            {
                failsafeActivated = true;
                break;
            }
        }

        var compositeDisposable = new CompositeDisposable(disposables, []);

        if (!failsafeActivated)
            return compositeDisposable;

        await compositeDisposable.DisposeAsync();
        throw new Exception("One or more locks could not be acquired, disposed all, exiting...");
    }
}