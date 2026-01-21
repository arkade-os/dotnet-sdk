namespace NArk.Abstractions.Batches.ServerEvents;

public record TreeTxEvent(string Id, int BatchIndex, Dictionary<uint, string> Children, IReadOnlyCollection<string> Topic, string Tx, string TxId) : BatchEvent;