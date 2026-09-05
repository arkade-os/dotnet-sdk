using NBitcoin;

namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>New batch opened; clients may now submit intents.</summary>
/// <param name="Id">Batch ID.</param>
/// <param name="BatchExpiry">Sequence value encoding when the batch closes.</param>
/// <param name="IntentIdHashes">SHA256 hashes of the included intent IDs (hex-encoded).</param>
/// <param name="RawBatchExpiry">
/// The expiry as the Arkade server declared it, before BIP-68 encoding: a block count when below 512,
/// otherwise seconds. Transports must supply this — <paramref name="BatchExpiry"/> floors seconds to a
/// multiple of 512, so only the raw value can be validated and reported faithfully.
/// </param>
public record BatchStartedEvent(
    string Id,
    Sequence BatchExpiry,
    IReadOnlyCollection<string> IntentIdHashes,
    long RawBatchExpiry) : BatchEvent;
