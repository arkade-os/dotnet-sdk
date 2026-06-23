namespace NArk.Core.CoinSelector.EARCoinSelector;

public interface ICoinSelectionStrategy
{
    SelectionStrategy Strategy { get; }
    SelectionResult? TrySelect(
        IReadOnlyList<ExpiryBucket> buckets,
        SelectionContext context,
        CoinSelectionPolicy policy);
}
