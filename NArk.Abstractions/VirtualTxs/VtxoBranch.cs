namespace NArk.Abstractions.VirtualTxs;

/// <summary>
/// Links a VTXO to one virtual tx in its exit branch, with position ordering.
/// Position 0 = the VTXO's own (leaf) tx, higher = further up the ancestry,
/// ending at the commitment tx (tree root). Do not identify the leaf/root by
/// array position when consuming a branch — match by <see cref="ChainedTxType.Commitment"/>
/// for the root, and by txid equal to the VTXO's own outpoint for the leaf.
/// </summary>
public record VtxoBranch(string VtxoTxid, uint VtxoVout, string VirtualTxid, int Position);
