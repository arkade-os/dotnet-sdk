using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed class BranchAndBoundStrategy : ICoinSelectionStrategy
{
    public SelectionStrategy Strategy => SelectionStrategy.BnB;

    public SelectionResult? TrySelect(
        IReadOnlyList<ExpiryBucket> buckets,
        SelectionContext context,
        CoinSelectionPolicy policy)
    {
        SelectionResult? best = null;

        foreach (var bucket in buckets)
        {
            if (bucket.Coins.Count > policy.MaxBnBInputs)
                continue;

            var eligible = context.AllowDustInputs
                ? bucket.Coins
                : bucket.Coins.Where(c => !c.IsDustProne).ToList();
            var result = SearchBnB(eligible, context, bucket.ExpiryGroup);
            best = PickBetter(best, result);

            if (best?.Change == Money.Zero)
                break;
        }

        return best;
    }

    private static SelectionResult? SearchBnB(
        IReadOnlyList<CoinCandidate> coins,
        SelectionContext context,
        uint expiryGroup)
    {
        var suffix = new Money[coins.Count + 1];
        suffix[coins.Count] = Money.Zero;
        for (var i = coins.Count - 1; i >= 0; i--)
            suffix[i] = suffix[i + 1] + coins[i].Value;

        if (suffix[0] < context.TargetAmount)
            return null;

        SelectionResult? best = null;
        Money? bestWaste = null;
        var included = new bool[coins.Count];

        void Dfs(int idx, Money sum)
        {
            if (sum >= context.TargetAmount)
            {
                var change = sum - context.TargetAmount;

                if (change > Money.Zero && change < context.DustThreshold && !context.AllowSubDust)
                    return;

                if (bestWaste is null || change < bestWaste)
                {
                    bestWaste = change;
                    var selected = new List<ArkCoin>(coins.Count);
                    for (var i = 0; i < coins.Count; i++)
                        if (included[i]) selected.Add(coins[i].Coin);

                    best = new SelectionResult(
                        SelectedCoins: selected,
                        TotalValue: sum,
                        Change: change,
                        ExpiryGroup: expiryGroup,
                        Strategy: SelectionStrategy.BnB,
                        Waste: change,
                        IsValid: true,
                        ExpiryMixedFallback: false);
                }
                return;
            }

            if (idx >= coins.Count)
                return;

            if (sum + suffix[idx] < context.TargetAmount)
                return;

            included[idx] = true;
            Dfs(idx + 1, sum + coins[idx].Value);
            included[idx] = false;

            if (sum + suffix[idx + 1] >= context.TargetAmount)
                Dfs(idx + 1, sum);
        }

        Dfs(0, Money.Zero);
        return best;
    }

    private static SelectionResult? PickBetter(SelectionResult? a, SelectionResult? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Waste <= b.Waste ? a : b;
    }
}
