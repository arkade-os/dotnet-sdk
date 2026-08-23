using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed record SelectionResult(
    IReadOnlyList<ArkCoin> SelectedCoins,
    Money TotalValue,
    Money Change,
    uint ExpiryGroup,
    SelectionStrategy Strategy,
    Money Waste,
    bool IsValid,
    bool ExpiryMixedFallback);