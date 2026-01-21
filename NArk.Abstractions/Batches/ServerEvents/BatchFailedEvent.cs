namespace NArk.Abstractions.Batches.ServerEvents;

public record BatchFailedEvent(string Id, string Reason) : BatchEvent;