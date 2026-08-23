namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>Server publishes its MuSig2 nonces for a batch tree transaction.</summary>
/// <param name="Id">Batch ID.</param>
/// <param name="Nonces">MuSig2 public nonces (hex) keyed by cosigner identifier; only values are consumed by the SDK.</param>
/// <param name="Topic">Stream topics this event targets.</param>
/// <param name="TxId">The tree transaction these nonces are for.</param>
public record TreeNoncesEvent(string Id, Dictionary<string, string> Nonces, IReadOnlyCollection<string> Topic, string TxId) : BatchEvent;
