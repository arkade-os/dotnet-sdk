namespace NArk.Abstractions.Batches;

/// <summary>
/// Submits the client's MuSig2 partial signatures for each batch tree transaction.
/// </summary>
/// <param name="BatchId">Batch the signatures belong to.</param>
/// <param name="PubKey">Client's compressed public key (hex).</param>
/// <param name="TreeSignatures">Map of txid → partial signature (hex).</param>
public record SubmitTreeSignaturesRequest(string BatchId, string PubKey, Dictionary<string, string> TreeSignatures);
