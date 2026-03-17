namespace NArk.Abstractions.VirtualTxs;

/// <summary>
/// Links a VTXO to one virtual tx in its exit branch, with position ordering.
/// Position 0 = closest to commitment tx (tree root), higher = closer to leaf.
/// </summary>
public record VtxoBranch(string VtxoTxid, uint VtxoVout, string VirtualTxid, int Position);
