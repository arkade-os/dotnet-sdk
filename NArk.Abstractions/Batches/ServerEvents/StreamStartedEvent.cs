namespace NArk.Abstractions.Batches.ServerEvents;

public record StreamStartedEvent(string StreamId) : BatchEvent;
