using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed class RgliStrategy : ICoinSelectionStrategy
{
    public SelectionStrategy Strategy => SelectionStrategy.RGLI;

    public SelectionResult? TrySelect(
        IReadOnlyList<ExpiryBucket> buckets,
        SelectionContext context,
        CoinSelectionPolicy policy)
    {
        SelectionResult? best = null;

        foreach (var bucket in buckets)
        {
            var result = RunRgli(bucket.Coins, context, policy, bucket.ExpiryGroup, expiryMixed: false);
            best = PickBetter(best, result);
        }

        if (policy.AllowExpiryMixingFallback)
        {
            var all = buckets.SelectMany(b => b.Coins).ToList();
            var mixed = RunRgli(all, context, policy, expiryGroup: 0u, expiryMixed: true);
            best = PickBetter(best, mixed);
        }

        return best;
    }

    private static SelectionResult? RunRgli(
        IReadOnlyList<CoinCandidate> coins,
        SelectionContext context,
        CoinSelectionPolicy policy,
        uint expiryGroup,
        bool expiryMixed)
    {
        SelectionResult? best = null;

        for (var i = 0; i < policy.RandomTopK; i++)
        {
            var shuffled = coins.OrderBy(_ => Random.Shared.Next()).ToList();
            var seed = GreedySeed(shuffled, context, expiryGroup, expiryMixed);
            if (seed is null)
                continue;

            var improved = LocalImprove(seed, coins, context, policy, expiryGroup, expiryMixed);
            best = PickBetter(best, improved);
        }

        return best;
    }

    private static SelectionResult? GreedySeed(
        List<CoinCandidate> coins,
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

            if (total >= context.TargetAmount)
                break;
        }

        if (total < context.TargetAmount)
            return null;

        var change = total - context.TargetAmount;

        if (change > Money.Zero && change < context.DustThreshold && !context.AllowSubDust)
            return null;

        return new SelectionResult(
            SelectedCoins: selected,
            TotalValue: total,
            Change: change,
            ExpiryGroup: expiryGroup,
            Strategy: SelectionStrategy.RGLI,
            Waste: change,
            IsValid: true,
            ExpiryMixedFallback: expiryMixed);
    }

    private static SelectionResult LocalImprove(
        SelectionResult result,
        IReadOnlyList<CoinCandidate> allCoins,
        SelectionContext context,
        CoinSelectionPolicy policy,
        uint expiryGroup,
        bool expiryMixed)
    {
        var selected = result.SelectedCoins.ToList();

        for (var iteration = 0; iteration < policy.MaxLocalSearchIterations; iteration++)
        {
            var improved = false;

            if (selected.Count > 1)
            {
                var smallest = selected.MinBy(c => c.TxOut.Value)!;
                var withoutSmallest = selected.Where(c => !ReferenceEquals(c, smallest)).ToList();
                var newTotal = withoutSmallest.Sum(c => c.TxOut.Value);
                var newChange = newTotal - context.TargetAmount;

                if (newTotal >= context.TargetAmount
                    && (newChange == Money.Zero || newChange >= context.DustThreshold || context.AllowSubDust)
                    && newChange < result.Change)
                {
                    selected = withoutSmallest;
                    improved = true;
                    continue;
                }
            }

            var selectedSet = selected.ToHashSet(ReferenceEqualityComparer.Instance);
            var unused = allCoins
                .Where(c => !selectedSet.Contains(c.Coin))
                .OrderBy(c => c.Value)
                .ToList();

            var currentTotal = selected.Sum(c => c.TxOut.Value);
            var currentChange = currentTotal - context.TargetAmount;

            foreach (var candidate in unused)
            {
                foreach (var toRemove in selected.OrderByDescending(c => c.TxOut.Value))
                {
                    if (candidate.Value >= toRemove.TxOut.Value)
                        break;

                    var swappedTotal = currentTotal - toRemove.TxOut.Value + candidate.Value;
                    var swappedChange = swappedTotal - context.TargetAmount;

                    if (swappedTotal >= context.TargetAmount
                        && (swappedChange == Money.Zero || swappedChange >= context.DustThreshold || context.AllowSubDust)
                        && swappedChange < currentChange)
                    {
                        selected = selected
                            .Where(c => !ReferenceEquals(c, toRemove))
                            .Append(candidate.Coin)
                            .ToList();
                        improved = true;
                        break;
                    }
                }

                if (improved)
                    break;
            }

            if (!improved)
                break;
        }

        var finalTotal = selected.Sum(c => c.TxOut.Value);
        var finalChange = finalTotal - context.TargetAmount;

        return new SelectionResult(
            SelectedCoins: selected,
            TotalValue: finalTotal,
            Change: finalChange,
            ExpiryGroup: expiryGroup,
            Strategy: SelectionStrategy.RGLI,
            Waste: finalChange,
            IsValid: true,
            ExpiryMixedFallback: expiryMixed);
    }

    private static SelectionResult? PickBetter(SelectionResult? a, SelectionResult? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Waste <= b.Waste ? a : b;
    }
}
