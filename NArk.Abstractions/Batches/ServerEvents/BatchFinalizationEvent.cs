namespace NArk.Abstractions.Batches.ServerEvents;

public record BatchFinalizationEvent(string CommitmentTx, string Id) : BatchEvent;