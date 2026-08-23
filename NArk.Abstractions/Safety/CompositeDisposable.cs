namespace NArk.Abstractions.Safety;

/// <summary>Disposes a group of sync and async disposables; exceptions from individual items are suppressed.</summary>
public class CompositeDisposable(IReadOnlyCollection<IDisposable> syncDisposables, IReadOnlyCollection<IAsyncDisposable> asyncDisposables)
: IDisposable, IAsyncDisposable
{
#pragma warning disable CS1591
    public void Dispose()
    {
        foreach (var disposable in syncDisposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        foreach (var disposable in asyncDisposables)
        {
            try
            {
                disposable.DisposeAsync().AsTask().RunSynchronously();
            }
            catch
            {
                // ignored
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in asyncDisposables)
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch
            {
                // ignored
            }
        }

        foreach (var disposable in syncDisposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // ignored
            }
        }
    }
#pragma warning restore CS1591
}