using NArk.Abstractions.Intents;
using NArk.Enums;

namespace NArk.Events;

public record PostBatchSessionEvent(
    ArkIntent Intent,
    string? CommitmentTransactionId,
    ActionState State,
    string? FailReason
);