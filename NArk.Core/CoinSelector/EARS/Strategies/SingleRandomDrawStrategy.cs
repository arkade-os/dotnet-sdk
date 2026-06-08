using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed class SingleRandomDrawStrategy : ICoinSelectionStrategy
{
    public SelectionStrategy Strategy => SelectionStrategy.SRD;

    public SelectionResult? TrySelect(
        IReadOnlyList<CoinCandidate> candidates,
        SelectionContext context,
        CoinSelectionPolicy policy)
    {
        var groups = candidates
            .GroupBy(c => c.ExpiryGroup)
            .OrderBy(g => g.Key);

        foreach (var group in groups)
        {
            if (group.Sum(c => c.Value) < context.TargetAmount)
                continue;

            var shuffled = group.OrderBy(_ => Random.Shared.Next()).ToList();
            return Greedy(shuffled, context, group.Key, expiryMixed: false);
        }

        if (!policy.AllowExpiryMixingFallback)
            return null;

        var all = candidates.OrderBy(_ => Random.Shared.Next()).ToList();
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
            Strategy: SelectionStrategy.SRD,
            Waste: change,
            IsValid: true,
            ExpiryMixedFallback: expiryMixed);
    }
}
