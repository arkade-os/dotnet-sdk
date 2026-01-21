namespace NArk.Abstractions.Safety;

public class CompositeDisposable(IReadOnlyCollection<IDisposable> syncDisposables, IReadOnlyCollection<IAsyncDisposable> asyncDisposables)
: IDisposable, IAsyncDisposable
{
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
}