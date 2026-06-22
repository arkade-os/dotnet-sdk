using NBitcoin;

namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>New batch opened; clients may now submit intents.</summary>
/// <param name="Id">Batch ID.</param>
/// <param name="BatchExpiry">Sequence value encoding when the batch closes.</param>
/// <param name="IntentIdHashes">SHA256 hashes of the included intent IDs (hex-encoded).</param>
public record BatchStartedEvent(string Id, Sequence BatchExpiry, IReadOnlyCollection<string> IntentIdHashes) : BatchEvent;
