using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed class CoinSelectionEngine
{
    private readonly IReadOnlyList<ICoinSelectionStrategy> _strategies;

    public CoinSelectionEngine(IReadOnlyList<ICoinSelectionStrategy> strategies)
    {
        _strategies = strategies;
    }

    public SelectionResult Select(
        IReadOnlyList<CoinCandidate> candidates,
        SelectionContext context,
        CoinSelectionPolicy policy)
    {
        // Phase 1: reserve coins that carry required assets (expiry-aware: earliest first).
        var (assetCoins, remainingCandidates, btcCoveredByAssets) =
            SelectAssetCoins(candidates, context.AssetRequirements);

        var remainingTarget = context.TargetAmount - btcCoveredByAssets;

        // Phase 2: if asset coins alone cover the BTC target, we're done.
        if (remainingTarget <= Money.Zero)
        {
            var total = assetCoins.Sum(c => c.TxOut.Value);
            return new SelectionResult(
                SelectedCoins: assetCoins,
                TotalValue: total,
                Change: total - context.TargetAmount,
                ExpiryGroup: assetCoins.Select(c => c.ExpiresAtHeight ?? 0u).DefaultIfEmpty(0u).Min(),
                Strategy: SelectionStrategy.ExpiryFirst,
                Waste: Money.Zero,
                IsValid: true,
                ExpiryMixedFallback: false);
        }

        // Phase 3: run strategies on remaining candidates to fill BTC gap.
        var btcContext = context with { TargetAmount = remainingTarget };
        var buckets = BuildBuckets(remainingCandidates, policy.ExpiryWindowBlocks);

        var valid = new List<SelectionResult>();
        foreach (var strategy in _strategies)
        {
            var result = strategy.TrySelect(buckets, btcContext, policy);
            if (result is not null && result.IsValid)
                valid.Add(result);
        }

        if (valid.Count == 0)
            throw new NotEnoughFundsException("No valid selection", null, context.TargetAmount);

        var best = valid.OrderBy(r => r.Waste).ThenBy(r => r.SelectedCoins.Count).First();

        // Phase 4: merge asset coins with BTC selection.
        if (assetCoins.Count == 0)
            return best;

        var merged = assetCoins.Concat(best.SelectedCoins).ToList();
        var mergedTotal = merged.Sum(c => c.TxOut.Value);
        return best with
        {
            SelectedCoins = merged,
            TotalValue = mergedTotal,
            Change = mergedTotal - context.TargetAmount,
        };
    }

    private static (List<ArkCoin> assetCoins, List<CoinCandidate> remaining, Money btcCovered)
        SelectAssetCoins(
            IReadOnlyList<CoinCandidate> candidates,
            IReadOnlyList<AssetRequirement> requirements)
    {
        if (requirements.Count == 0)
            return ([], candidates.ToList(), Money.Zero);

        var reserved = new HashSet<CoinCandidate>(ReferenceEqualityComparer.Instance);

        foreach (var req in requirements)
        {
            var eligible = candidates
                .Where(c => !reserved.Contains(c)
                    && c.Assets.Any(a => a.AssetId == req.AssetId))
                .OrderBy(c => c.ExpiryGroup)
                .ThenBy(c => c.Assets.First(a => a.AssetId == req.AssetId).Amount)
                .ToList();

            var assetTotal = 0UL;
            foreach (var coin in eligible)
            {
                if (assetTotal >= req.Amount)
                    break;
                reserved.Add(coin);
                assetTotal += coin.Assets.First(a => a.AssetId == req.AssetId).Amount;
            }

            if (assetTotal < req.Amount)
                throw new NotEnoughFundsException(
                    $"Not enough {req.AssetId}: have {assetTotal}, need {req.Amount}",
                    null, Money.Zero);
        }

        var assetCoins = reserved.Select(c => c.Coin).ToList();
        var remaining = candidates.Where(c => !reserved.Contains(c)).ToList();
        var btcCovered = reserved.Sum(c => c.Value);

        return (assetCoins, remaining, btcCovered);
    }

    // Groups candidates into buckets where all coins within a bucket expire within
    // ExpiryWindowBlocks of the earliest coin in that bucket (~24h by default).
    // Coins with no expiry height (ExpiryGroup == 0) form their own bucket.
    internal static IReadOnlyList<ExpiryBucket> BuildBuckets(
        IReadOnlyList<CoinCandidate> candidates,
        uint windowBlocks)
    {
        var sorted = candidates.OrderBy(c => c.ExpiryGroup).ToList();
        var buckets = new List<ExpiryBucket>();
        var current = new List<CoinCandidate>();
        var windowStart = 0u;

        foreach (var coin in sorted)
        {
            if (current.Count == 0)
            {
                current.Add(coin);
                windowStart = coin.ExpiryGroup;
            }
            else if (windowStart == 0u || coin.ExpiryGroup - windowStart <= windowBlocks)
            {
                current.Add(coin);
            }
            else
            {
                buckets.Add(MakeBucket(current));
                current = [coin];
                windowStart = coin.ExpiryGroup;
            }
        }

        if (current.Count > 0)
            buckets.Add(MakeBucket(current));

        return buckets;
    }

    private static ExpiryBucket MakeBucket(List<CoinCandidate> coins) =>
        new(ExpiryGroup: coins.Min(c => c.ExpiryGroup),
            Coins: coins.OrderByDescending(c => c.Value).ToList(),
            TotalValue: coins.Sum(c => c.Value));
}
