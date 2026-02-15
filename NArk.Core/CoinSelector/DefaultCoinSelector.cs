// Should be refactored, this is a copy from old Nark


using NArk.Abstractions;
using NArk.Core.Helpers;
using NBitcoin;

namespace NArk.Core.CoinSelector;

public class DefaultCoinSelector : ICoinSelector
{
    /// <summary>
    /// Selects coins to minimize sub-dust change. Prefers exact matches or combinations that avoid subdust change.
    /// </summary>
    /// <param name="availableCoins">Available coins sorted by value descending</param>
    /// <param name="targetAmount">Target amount to send</param>
    /// <param name="dustThreshold">Dust threshold from operator terms</param>
    /// <param name="currentSubDustOutputs">Whether the user explicitly uses subdust change</param>
    /// <returns>Selected coins or null if impossible</returns>
    public IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetAmount,
        Money dustThreshold,
        int currentSubDustOutputs)
    {
        if (availableCoins.Count == 0)
            throw new NotEnoughFundsException("Not enough funds to create transaction", null, targetAmount);

        var totalAvailable = availableCoins.Sum(x => x.TxOut.Value);
        if (totalAvailable < targetAmount)
            throw new NotEnoughFundsException("Not enough funds to create transaction", null, targetAmount - totalAvailable);

        // Strategy 1: Try to find an exact match or match with change > dust
        // Start with largest coins first (greedy approach)
        var selected = new List<ArkCoin>();
        var currentTotal = Money.Zero;

        foreach (var coin in availableCoins)
        {
            if (currentTotal >= targetAmount)
            {
                var change = currentTotal - targetAmount;
                // Check if the change is acceptable (either 0, > dust, or we can add another subdust OP_RETURN)
                var canAddSubdustChange = (currentSubDustOutputs + 1) <= TransactionHelpers.MaxOpReturnOutputs;
                if (change == Money.Zero || change >= dustThreshold || canAddSubdustChange)
                    break;
            }

            selected.Add(coin);
            currentTotal += coin.TxOut.Value;
        }

        var finalChange = currentTotal - targetAmount;

        // If we have subdust change and can't add another OP_RETURN, try to find better combination
        var canAddSubdust = (currentSubDustOutputs + 1) <= TransactionHelpers.MaxOpReturnOutputs;
        if (finalChange > Money.Zero && finalChange < dustThreshold && !canAddSubdust)
        {
            // Strategy 2: Try adding one more coin to push change above dust threshold
            var remainingCoins = availableCoins.Except(selected).ToList();
            foreach (var extraCoin in remainingCoins)
            {
                var newChange = finalChange + extraCoin.TxOut.Value;
                if (newChange >= dustThreshold)
                {
                    selected.Add(extraCoin);
                    return selected;
                }
            }

            // Strategy 3: Try to find a combination that results in no change or change > dust
            var betterSelection = TryFindBetterCombination(availableCoins, targetAmount, dustThreshold);
            if (betterSelection != null)
            {
                return betterSelection;
            }

            // Strategy 4: If we can't avoid subdust, use all coins to maximize change
            return availableCoins;
        }

        return selected;
    }

    /// <summary>
    /// Asset-aware coin selection. First selects coins carrying required assets,
    /// then fills remaining BTC from plain coins using the standard selection logic.
    /// </summary>
    public IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetBtcAmount,
        IReadOnlyList<AssetRequirement> assetRequirements,
        Money dustThreshold,
        int currentSubDustOutputs)
    {
        if (assetRequirements.Count == 0)
            return SelectCoins(availableCoins, targetBtcAmount, dustThreshold, currentSubDustOutputs);

        var selected = new HashSet<ArkCoin>(ReferenceEqualityComparer.Instance);
        var btcFromAssetCoins = Money.Zero;

        foreach (var requirement in assetRequirements)
        {
            var assetCoins = availableCoins
                .Where(c => !selected.Contains(c) && c.Assets is { Count: > 0 } assets
                    && assets.Any(a => a.AssetId == requirement.AssetId))
                .OrderBy(c => c.Assets!.First(a => a.AssetId == requirement.AssetId).Amount)
                .ToList();

            var assetTotal = 0UL;
            foreach (var coin in assetCoins)
            {
                if (assetTotal >= requirement.Amount)
                    break;

                selected.Add(coin);
                btcFromAssetCoins += coin.TxOut.Value;
                assetTotal += coin.Assets!.First(a => a.AssetId == requirement.AssetId).Amount;
            }

            if (assetTotal < requirement.Amount)
                throw new NotEnoughFundsException(
                    $"Not enough {requirement.AssetId} funds: have {assetTotal}, need {requirement.Amount}",
                    null, Money.Zero);
        }

        var remainingBtc = targetBtcAmount - btcFromAssetCoins;
        if (remainingBtc <= Money.Zero)
            return selected.ToList();

        var remainingCoins = availableCoins.Where(c => !selected.Contains(c)).ToList();
        if (remainingCoins.Count == 0 || remainingCoins.Sum(c => c.TxOut.Value) < remainingBtc)
        {
            if (selected.Sum(c => c.TxOut.Value) >= targetBtcAmount)
                return selected.ToList();
            throw new NotEnoughFundsException("Not enough BTC funds to create transaction", null, remainingBtc);
        }

        var btcSelected = SelectCoins(remainingCoins, remainingBtc, dustThreshold, currentSubDustOutputs);
        foreach (var coin in btcSelected)
            selected.Add(coin);

        return selected.ToList();
    }

    /// <summary>
    /// Attempts to find a better coin combination that avoids subdust change
    /// </summary>
    private List<ArkCoin>? TryFindBetterCombination(
        List<ArkCoin> availableCoins,
        Money targetAmount,
        Money dustThreshold)
    {
        // Try combinations of 1-3 coins (to keep it performant)
        // Look for the exact match first
        foreach (var coin in availableCoins)
        {
            if (coin.TxOut.Value == targetAmount)
                return [coin];
        }

        // Try pairs
        for (var i = 0; i < availableCoins.Count; i++)
        {
            for (var j = i + 1; j < availableCoins.Count; j++)
            {
                var total = availableCoins[i].TxOut.Value + availableCoins[j].TxOut.Value;
                if (total < targetAmount)
                    continue;

                var change = total - targetAmount;
                if (change == Money.Zero || change >= dustThreshold)
                    return [availableCoins[i], availableCoins[j]];
            }
        }

        // Try triplets
        for (var i = 0; i < availableCoins.Count && i < 10; i++) // Limit to first 10 for performance
        {
            for (var j = i + 1; j < availableCoins.Count && j < 10; j++)
            {
                for (var k = j + 1; k < availableCoins.Count && k < 10; k++)
                {
                    var total = availableCoins[i].TxOut.Value + availableCoins[j].TxOut.Value + availableCoins[k].TxOut.Value;
                    if (total < targetAmount)
                        continue;

                    var change = total - targetAmount;
                    if (change == Money.Zero || change >= dustThreshold)
                        return [availableCoins[i], availableCoins[j], availableCoins[k]];
                }
            }
        }

        return null;
    }
}