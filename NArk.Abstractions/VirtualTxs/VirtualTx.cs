namespace NArk.Abstractions.VirtualTxs;

/// <summary>
/// A single virtual transaction in the VTXO tree.
/// </summary>
/// <param name="Txid">Transaction id (hex).</param>
/// <param name="Hex">PSBT-encoded transaction body, or null when only the
/// txid + metadata are stored (Lite mode, or for on-chain commitments
/// the indexer doesn't expose hex for).</param>
/// <param name="ExpiresAt">When the operator's pre-signature on this tx
/// expires; null if not applicable (e.g. a confirmed commitment tx).</param>
/// <param name="Type">Tx type as reported by arkd's chain indexer. Lets
/// consumers tell apart the on-chain commitment root from the off-chain
/// tree / Ark / checkpoint nodes without re-querying the indexer.</param>
public record VirtualTx(
    string Txid,
    string? Hex,
    DateTimeOffset? ExpiresAt,
    ChainedTxType Type = ChainedTxType.Unspecified);
