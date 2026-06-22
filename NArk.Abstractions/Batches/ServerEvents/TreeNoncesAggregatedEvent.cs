namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>Server has aggregated all cosigner nonces for the batch tree.</summary>
/// <param name="Id">Batch ID.</param>
/// <param name="TreeNonces">Map of txid → aggregated nonce (hex).</param>
public record TreeNoncesAggregatedEvent(string Id, Dictionary<string, string> TreeNonces) : BatchEvent;
