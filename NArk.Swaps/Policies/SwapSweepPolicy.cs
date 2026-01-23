using System.Runtime.CompilerServices;
using NArk.Abstractions;
using NArk.Core.Contracts;
using NArk.Core.Sweeper;

namespace NArk.Swaps.Policies;

public class SwapSweepPolicy : ISweepPolicy
{
    public async IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        coins = coins.Where(c => c.Contract is VHTLCContract);
        foreach (var coin in coins)
        {
            yield return coin;
        }
    }
}