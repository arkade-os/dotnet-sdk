namespace NArk.Abstractions.Intents;

public enum ArkIntentState
{
    WaitingToSubmit,
    WaitingForBatch,
    BatchInProgress,
    BatchFailed,
    BatchSucceeded,
    Cancelled
}