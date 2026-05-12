namespace NArk.Abstractions.VirtualTxs;

/// <summary>
/// Type of a transaction inside a VTXO's chain (commitment → leaf).
/// Carried on every <see cref="VirtualTx"/> record so consumers can
/// distinguish on-chain anchors (<see cref="Commitment"/>) from the
/// off-chain virtual txs in the tree (<see cref="Tree"/> /
/// <see cref="Ark"/> / <see cref="Checkpoint"/>) without re-querying
/// the indexer.
/// </summary>
public enum ChainedTxType
{
    /// <summary>Type was not provided by the indexer.</summary>
    Unspecified = 0,

    /// <summary>The on-chain commitment transaction at the root of the
    /// batch tree. Already broadcast and confirmed by the operator.</summary>
    Commitment = 1,

    /// <summary>An off-chain Ark transaction (preconfirmed VTXO move).</summary>
    Ark = 2,

    /// <summary>An intermediate node in the VTXO tree that descends from
    /// the commitment tx down to a leaf VTXO.</summary>
    Tree = 3,

    /// <summary>A checkpoint transaction used to anchor / roll up a
    /// portion of the tree.</summary>
    Checkpoint = 4,
}
