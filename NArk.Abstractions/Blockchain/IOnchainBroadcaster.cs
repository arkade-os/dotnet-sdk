using NBitcoin;

namespace NArk.Abstractions.Blockchain;

/// <summary>
/// Broadcasts Bitcoin transactions, including v3 package relay.
/// </summary>
public interface IOnchainBroadcaster
{
    /// <summary>
    /// Broadcast a single transaction.
    /// </summary>
    Task<bool> BroadcastAsync(Transaction tx, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast a 1p1c package (parent + CPFP child) via submitpackage.
    /// </summary>
    Task<bool> BroadcastPackageAsync(Transaction parent, Transaction child, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get transaction status on-chain.
    /// </summary>
    Task<TxStatus> GetTxStatusAsync(uint256 txid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Estimate fee rate for the given confirmation target.
    /// </summary>
    Task<FeeRate> EstimateFeeRateAsync(int confirmTarget = 6, CancellationToken cancellationToken = default);
}

/// <summary>
/// On-chain transaction status.
/// </summary>
public record TxStatus(bool Confirmed, uint? BlockHeight, bool InMempool);
