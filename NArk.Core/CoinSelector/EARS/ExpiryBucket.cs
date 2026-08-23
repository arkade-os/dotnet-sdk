using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

/// <summary>
/// A group of coin candidates that share the same expiry height (i.e. belong to the same Arkade batch).
/// Coins within the bucket are pre-sorted by value descending.
/// </summary>
public sealed record ExpiryBucket(
    uint ExpiryGroup,
    IReadOnlyList<CoinCandidate> Coins,
    Money TotalValue);
