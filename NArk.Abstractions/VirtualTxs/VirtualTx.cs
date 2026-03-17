namespace NArk.Abstractions.VirtualTxs;

/// <summary>
/// A single virtual transaction in the VTXO tree.
/// </summary>
public record VirtualTx(string Txid, string? Hex, DateTimeOffset? ExpiresAt);
