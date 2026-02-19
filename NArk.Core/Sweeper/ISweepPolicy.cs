using NArk.Abstractions;

namespace NArk.Core.Sweeper;

/// <summary>
/// Determines which coins should be consolidated (swept) into a single VTXO.
/// Register multiple policies to combine different consolidation strategies
/// (e.g., time-based expiry, dust collection, fee optimization).
/// </summary>
public interface ISweepPolicy
{
    /// <summary>
    /// Yields coins that should be swept from the given set.
    /// </summary>
    public IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins, CancellationToken cancellationToken = default);
}