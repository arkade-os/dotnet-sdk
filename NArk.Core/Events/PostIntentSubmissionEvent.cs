using NArk.Abstractions.Intents;
using NArk.Core.Enums;

namespace NArk.Core.Events;

public record PostIntentSubmissionEvent(
    ArkIntent Intent,
    DateTimeOffset SubmissionTime,
    bool FirstTry,
    ActionState State,
    string? FailReason
);