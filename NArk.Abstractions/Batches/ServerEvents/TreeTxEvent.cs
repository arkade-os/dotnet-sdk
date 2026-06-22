namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>Server-published virtual transaction node in the batch tree.</summary>
/// <param name="Id">Batch ID.</param>
/// <param name="BatchIndex">Which subtree this node belongs to: 0 = VTXO tree, 1 = connectors tree.</param>
/// <param name="Children">Map of output-index → child txid.</param>
/// <param name="Topic">Stream topics this event targets.</param>
/// <param name="Tx">PSBT of this tree node (base64).</param>
/// <param name="TxId">Transaction ID of this node.</param>
public record TreeTxEvent(
    string Id,
    int BatchIndex,
    Dictionary<uint, string> Children,
    IReadOnlyCollection<string> Topic,
    string Tx,
    string TxId) : BatchEvent;
