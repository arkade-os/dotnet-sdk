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
            var eligible = context.AllowDustInputs
                ? bucket.Coins
                : bucket.Coins.Where(c => !c.IsDustProne).ToList();

            if (eligible.Count > policy.MaxBnBInputs)
                continue;

            var result = SearchBnB(eligible, context, policy, bucket.ExpiryGroup, expiryMixed: false);
            best = PickBetter(best, result);

            // Single-input exact match is the global minimum waste — no point searching further.
            if (best is { Change.Satoshi: 0, SelectedCoins.Count: 1 })
                return best;
        }

        if (policy.AllowExpiryMixingFallback)
        {
            var allEligible = buckets
                .SelectMany(b => b.Coins)
                .Where(c => context.AllowDustInputs || !c.IsDustProne)
                .OrderByDescending(c => c.Value)
                .ToList();

            if (allEligible.Count <= policy.MaxBnBInputs)
            {
                var mixed = SearchBnB(allEligible, context, policy, expiryGroup: 0u, expiryMixed: true);
                best = PickBetter(best, mixed);
            }
        }

        return best;
    }

    private static SelectionResult? SearchBnB(
        IReadOnlyList<CoinCandidate> coins,
        SelectionContext context,
        CoinSelectionPolicy policy,
        uint expiryGroup,
        bool expiryMixed)
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

        void Dfs(int idx, Money sum, int depth)
        {
            if (sum >= context.TargetAmount)
            {
                var change = sum - context.TargetAmount;

                if (change > Money.Zero && change < context.DustThreshold && !context.AllowSubDust)
                    return;

                var selected = new List<ArkCoin>(depth);
                for (var i = 0; i < coins.Count; i++)
                    if (included[i]) selected.Add(coins[i].Coin);

                var waste = CoinSelectionEngine.ComputeWaste(change, selected.Count, policy);

                if (bestWaste is null || waste < bestWaste)
                {
                    bestWaste = waste;
                    best = new SelectionResult(
                        SelectedCoins: selected,
                        TotalValue: sum,
                        Change: change,
                        ExpiryGroup: expiryGroup,
                        Strategy: SelectionStrategy.BnB,
                        Waste: waste,
                        IsValid: true,
                        ExpiryMixedFallback: expiryMixed);
                }
                return;
            }

            if (idx >= coins.Count)
                return;

            if (sum + suffix[idx] < context.TargetAmount)
                return;

            if (depth >= context.MaxInputs)
                return;

            included[idx] = true;
            Dfs(idx + 1, sum + coins[idx].Value, depth + 1);
            included[idx] = false;

            if (sum + suffix[idx + 1] >= context.TargetAmount)
                Dfs(idx + 1, sum, depth);
        }

        Dfs(0, Money.Zero, 0);
        return best;
    }

    private static SelectionResult? PickBetter(SelectionResult? a, SelectionResult? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Waste <= b.Waste ? a : b;
    }
}
