namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>Server aborted the batch. <see cref="Reason"/> describes why.</summary>
public record BatchFailedEvent(string Id, string Reason) : BatchEvent;
