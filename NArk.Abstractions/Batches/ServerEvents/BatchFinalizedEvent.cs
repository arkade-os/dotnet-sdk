namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>Batch commitment transaction has been broadcast on-chain.</summary>
/// <param name="CommitmentTxId">Txid of the confirmed commitment transaction.</param>
/// <param name="Id">Batch ID.</param>
public record BatchFinalizedEvent(string CommitmentTxId, string Id) : BatchEvent;
