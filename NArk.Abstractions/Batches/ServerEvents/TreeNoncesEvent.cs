namespace NArk.Abstractions.Batches.ServerEvents;

public record TreeNoncesEvent(string Id, Dictionary<string, string> Nonces, IReadOnlyCollection<string> Topic, string TxId) : BatchEvent;