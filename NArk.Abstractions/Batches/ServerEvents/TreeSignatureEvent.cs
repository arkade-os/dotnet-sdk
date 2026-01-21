namespace NArk.Abstractions.Batches.ServerEvents;

public record TreeSignatureEvent(int BatchIndex, string Id, string Signature, IReadOnlyCollection<string> Topic, string TxId) : BatchEvent;