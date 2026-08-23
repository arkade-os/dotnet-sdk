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
        // Fall through to Phase 3 if the change would be sub-dust — strategies can find
        // additional coins to push change above the threshold.
        if (remainingTarget <= Money.Zero)
        {
            var total = assetCoins.Sum(c => c.TxOut.Value);
            var change = total - context.TargetAmount;
            var subDust = change > Money.Zero && change < context.DustThreshold && !context.AllowSubDust;

            if (!subDust)
                return new SelectionResult(
                    SelectedCoins: assetCoins,
                    TotalValue: total,
                    Change: change,
                    ExpiryGroup: assetCoins.Select(c => c.ExpiresAtHeight ?? 0u).DefaultIfEmpty(0u).Min(),
                    Strategy: SelectionStrategy.ExpiryFirst,
                    Waste: ComputeWaste(change, assetCoins.Count, policy),
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

        var best = valid.MinBy(r => r.Waste)!;

        // Phase 4: merge asset coins with BTC selection.
        if (assetCoins.Count == 0)
            return best;

        var merged = assetCoins.Concat(best.SelectedCoins).ToList();
        var mergedTotal = merged.Sum(c => c.TxOut.Value);
        var mergedChange = mergedTotal - context.TargetAmount;
        return best with
        {
            SelectedCoins = merged,
            TotalValue = mergedTotal,
            Change = mergedChange,
            Waste = ComputeWaste(mergedChange, merged.Count, policy),
        };
    }

    // TODO: greedy multi-asset allocation has a known failure case: a coin carrying assets A and B
    // gets claimed by the first requirement that matches it, leaving the second requirement unsatisfied
    // even when a valid assignment exists (e.g. route coin-A→req-X, coin-B→req-Y).
    // For the 99% case (single-asset sends) this is fine. A proper fix requires backtracking or
    // a max-flow assignment — out of scope for now.
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
    // Coins with no expiry height (ExpiryGroup == 0) are separated before windowing —
    // mixing them into the loop would set windowStart=0 and collapse all coins into one bucket.
    // No-expiry coins go last: prefer spending expiring VTXOs first.
    internal static IReadOnlyList<ExpiryBucket> BuildBuckets(
        IReadOnlyList<CoinCandidate> candidates,
        uint windowBlocks)
    {
        var noExpiry = candidates.Where(c => c.ExpiryGroup == 0u).ToList();
        var withExpiry = candidates
            .Where(c => c.ExpiryGroup != 0u)
            .OrderBy(c => c.ExpiryGroup)
            .ToList();

        var buckets = new List<ExpiryBucket>();
        var current = new List<CoinCandidate>();
        var windowStart = 0u;

        foreach (var coin in withExpiry)
        {
            if (current.Count == 0)
            {
                current.Add(coin);
                windowStart = coin.ExpiryGroup;
            }
            else if (coin.ExpiryGroup - windowStart <= windowBlocks)
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

        if (noExpiry.Count > 0)
            buckets.Add(MakeBucket(noExpiry));

        return buckets;
    }

    internal static Money ComputeWaste(Money change, int inputCount, CoinSelectionPolicy policy) =>
        change + Money.Satoshis(inputCount * policy.CostPerInputSats);

    private static ExpiryBucket MakeBucket(List<CoinCandidate> coins) =>
        new(ExpiryGroup: coins.Min(c => c.ExpiryGroup),
            Coins: coins.OrderByDescending(c => c.Value).ToList(),
            TotalValue: coins.Sum(c => c.Value));
}
