using NArk.Abstractions.Intents;
using NArk.Enums;

namespace NArk.Events;

public record PostIntentSubmissionEvent(
    ArkIntent Intent,
    DateTimeOffset SubmissionTime,
    bool FirstTry,
    ActionState State,
    string? FailReason
);