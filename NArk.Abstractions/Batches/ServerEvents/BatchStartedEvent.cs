using NBitcoin;

namespace NArk.Abstractions.Batches.ServerEvents;

public record BatchStartedEvent(string Id, Sequence BatchExpiry, IReadOnlyCollection<string> IntentIdHashes) : BatchEvent;