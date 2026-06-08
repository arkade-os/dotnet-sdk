using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed class SingleRandomDrawStrategy : ICoinSelectionStrategy
{
    public SelectionStrategy Strategy => SelectionStrategy.SRD;

    public SelectionResult? TrySelect(
        IReadOnlyList<ExpiryBucket> buckets,
        SelectionContext context,
        CoinSelectionPolicy policy)
    {
        foreach (var bucket in buckets)
        {
            if (bucket.TotalValue < context.TargetAmount)
                continue;

            var shuffled = bucket.Coins.OrderBy(_ => Random.Shared.Next()).ToList();
            return Greedy(shuffled, context, bucket.ExpiryGroup, expiryMixed: false);
        }

        if (!policy.AllowExpiryMixingFallback)
            return null;

        var all = buckets.SelectMany(b => b.Coins).OrderBy(_ => Random.Shared.Next()).ToList();
        return Greedy(all, context, expiryGroup: 0u, expiryMixed: true);
    }

    private static SelectionResult? Greedy(
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

            if (total < context.TargetAmount)
                continue;

            var change = total - context.TargetAmount;

            if (change == Money.Zero || change >= context.DustThreshold || context.AllowSubDust)
                return new SelectionResult(
                    SelectedCoins: selected,
                    TotalValue: total,
                    Change: change,
                    ExpiryGroup: expiryGroup,
                    Strategy: SelectionStrategy.SRD,
                    Waste: change,
                    IsValid: true,
                    ExpiryMixedFallback: expiryMixed);
        }

        if (total < context.TargetAmount)
            return null;

        var finalChange = total - context.TargetAmount;

        if (finalChange > Money.Zero && finalChange < context.DustThreshold && !context.AllowSubDust)
            return null;

        return new SelectionResult(
            SelectedCoins: selected,
            TotalValue: total,
            Change: finalChange,
            ExpiryGroup: expiryGroup,
            Strategy: SelectionStrategy.SRD,
            Waste: finalChange,
            IsValid: true,
            ExpiryMixedFallback: expiryMixed);
    }
}
