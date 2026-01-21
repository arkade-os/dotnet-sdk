using NArk.Abstractions.Intents;
using NArk.Core.Enums;

namespace NArk.Core.Events;

public record PostBatchSessionEvent(
    ArkIntent Intent,
    string? CommitmentTransactionId,
    ActionState State,
    string? FailReason
);