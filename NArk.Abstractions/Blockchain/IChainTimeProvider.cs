namespace NArk.Abstractions.Blockchain;

/// <summary>
/// Provides the current Bitcoin blockchain height and timestamp.
/// Used to determine VTXO spendability based on timelock conditions.
/// </summary>
public interface IChainTimeProvider
{
    /// <summary>
    /// Returns the current chain time (block height and timestamp).
    /// </summary>
    Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default);
}