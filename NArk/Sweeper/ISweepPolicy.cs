using NArk.Abstractions;

namespace NArk.Sweeper;

public interface ISweepPolicy
{
    public IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins, CancellationToken cancellationToken = default);
}