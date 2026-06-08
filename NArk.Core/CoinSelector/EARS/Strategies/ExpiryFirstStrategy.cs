using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed class ExpiryFirstStrategy : ICoinSelectionStrategy
{
    public SelectionStrategy Strategy => SelectionStrategy.ExpiryFirst;

    public SelectionResult? TrySelect(
        IReadOnlyList<ExpiryBucket> buckets,
        SelectionContext context,
        CoinSelectionPolicy policy)
    {
        foreach (var bucket in buckets)
        {
            var result = GreedyWithinGroup(bucket.Coins, context, bucket.ExpiryGroup, expiryMixed: false);
            if (result is not null)
                return result;
        }

        if (!policy.AllowExpiryMixingFallback)
            return null;

        var all = buckets.SelectMany(b => b.Coins).OrderByDescending(c => c.Value).ToList();
        return GreedyWithinGroup(all, context, expiryGroup: 0u, expiryMixed: true);
    }

    private static SelectionResult? GreedyWithinGroup(
        IReadOnlyList<CoinCandidate> coins,
        SelectionContext context,
        uint expiryGroup,
        bool expiryMixed)
    {
        var selected = new List<ArkCoin>();
        var total = Money.Zero;

        foreach (var coin in coins)
        {
            if (selected.Count >= context.MaxInputs)
                break;

            if (coin.IsDustProne && !context.AllowDustInputs)
                continue;

            selected.Add(coin.Coin);
            total += coin.Value;

            if (total < context.TargetAmount)
                continue;

            var change = total - context.TargetAmount;

            // If change is above dust (or zero, or sub-dust is allowed), we're done.
            // Otherwise keep adding coins — each extra coin increases change, eventually escaping sub-dust.
            if (change == Money.Zero || change >= context.DustThreshold || context.AllowSubDust)
                return new SelectionResult(
                    SelectedCoins: selected,
                    TotalValue: total,
                    Change: change,
                    ExpiryGroup: expiryGroup,
                    Strategy: SelectionStrategy.ExpiryFirst,
                    Waste: change,
                    IsValid: true,
                    ExpiryMixedFallback: expiryMixed);
        }

        if (total < context.TargetAmount)
            return null;

        // Ran out of coins and still in sub-dust territory
        var finalChange = total - context.TargetAmount;

        if (finalChange > Money.Zero && finalChange < context.DustThreshold && !context.AllowSubDust)
            return null;

        return new SelectionResult(
            SelectedCoins: selected,
            TotalValue: total,
            Change: finalChange,
            ExpiryGroup: expiryGroup,
            Strategy: SelectionStrategy.ExpiryFirst,
            Waste: finalChange,
            IsValid: true,
            ExpiryMixedFallback: expiryMixed);
    }
}
