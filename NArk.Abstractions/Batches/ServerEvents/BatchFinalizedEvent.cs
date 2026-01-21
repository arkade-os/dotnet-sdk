namespace NArk.Abstractions.Batches.ServerEvents;

public record BatchFinalizedEvent(string CommitmentTxId, string Id) : BatchEvent;