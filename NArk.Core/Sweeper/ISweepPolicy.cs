using NArk.Abstractions;

namespace NArk.Core.Sweeper;

public interface ISweepPolicy
{
    public IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins, CancellationToken cancellationToken = default);
}