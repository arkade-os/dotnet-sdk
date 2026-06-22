namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>Server's MuSig2 partial signature for a single batch tree transaction.</summary>
/// <param name="BatchIndex">Which subtree this signature is for: 0 = VTXO tree, 1 = connectors tree.</param>
/// <param name="Id">Batch ID.</param>
/// <param name="Signature">Partial signature (hex).</param>
/// <param name="Topic">Stream topics this event targets.</param>
/// <param name="TxId">The tree transaction being signed.</param>
public record TreeSignatureEvent(int BatchIndex, string Id, string Signature, IReadOnlyCollection<string> Topic, string TxId) : BatchEvent;
