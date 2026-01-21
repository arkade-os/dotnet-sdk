namespace NArk.Abstractions.Batches.ServerEvents;

public record TreeNoncesAggregatedEvent(string Id, Dictionary<string, string> TreeNonces) : BatchEvent;