namespace NArk.Abstractions.VTXOs;

/// <summary>An Arkade issued asset balance attached to a VTXO.</summary>
public record VtxoAsset(string AssetId, ulong Amount);
