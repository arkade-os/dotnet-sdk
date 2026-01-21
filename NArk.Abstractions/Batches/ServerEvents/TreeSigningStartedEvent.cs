namespace NArk.Abstractions.Batches.ServerEvents;

public record TreeSigningStartedEvent(string UnsignedCommitmentTx, string Id, string[] CosignersPubkeys) : BatchEvent;