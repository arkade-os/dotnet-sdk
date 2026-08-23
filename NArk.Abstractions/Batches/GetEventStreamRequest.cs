namespace NArk.Abstractions.Batches;

/// <summary>Subscription request for the Arkade server event stream, filtered to the given topics.</summary>
public record GetEventStreamRequest(string[] Topics);
