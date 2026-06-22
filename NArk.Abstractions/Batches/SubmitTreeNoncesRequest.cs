namespace NArk.Abstractions.Batches;

/// <summary>
/// Submits the client's MuSig2 public nonces for each batch tree transaction.
/// </summary>
/// <param name="BatchId">Batch the nonces belong to.</param>
/// <param name="PubKey">Client's compressed public key (hex).</param>
/// <param name="Nonces">Map of txid → public nonce (hex).</param>
public record SubmitTreeNoncesRequest(string BatchId, string PubKey, Dictionary<string, string> Nonces);
