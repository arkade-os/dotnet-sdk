namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>First event on a new event-stream connection, carrying the server-assigned stream ID.</summary>
public record StreamStartedEvent(string StreamId) : BatchEvent;
