using NBitcoin;

namespace NArk.Abstractions.VirtualTxs;

/// <summary>
/// Storage for virtual transactions and their association with VTXOs.
/// </summary>
public interface IVirtualTxStorage
{
    /// <summary>
    /// Upsert virtual transaction records. If a tx already exists, updates non-null fields.
    /// </summary>
    Task UpsertVirtualTxsAsync(IReadOnlyList<VirtualTx> txs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Set the branch (ordered list of virtual txids) for a VTXO. Replaces any existing branch.
    /// </summary>
    Task SetBranchAsync(OutPoint vtxoOutpoint, IReadOnlyList<VtxoBranch> branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the virtual txs in a VTXO's exit branch, ordered by position.
    /// </summary>
    Task<IReadOnlyList<VirtualTx>> GetBranchAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single virtual tx by txid.
    /// </summary>
    Task<VirtualTx?> GetVirtualTxAsync(string txid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove branch entries for a spent VTXO, then clean up orphaned VirtualTx rows.
    /// </summary>
    Task PruneForSpentVtxoAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check whether a VTXO has a stored branch.
    /// </summary>
    Task<bool> HasBranchAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default);
}
