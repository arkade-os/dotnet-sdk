using NArk.Abstractions;
using NArk.Arkade.Contracts;
using NArk.Core.Sweeper;

namespace NArk.ArkadeIntents;

public class StaleSwapSweepPolicy: ISweepPolicy
{
    public async IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins, CancellationToken cancellationToken = default)
    {
        coins = coins.Where(c => c.Contract is VHTLCv2Contract);
        foreach (var coin in coins)
        {
            yield return coin;
        }
    }
}