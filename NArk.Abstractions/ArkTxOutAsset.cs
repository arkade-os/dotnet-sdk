namespace NArk.Abstractions;

/// <summary>An Ark-issued asset balance attached to a transaction output.</summary>
public record ArkTxOutAsset(string AssetId, ulong Amount);
