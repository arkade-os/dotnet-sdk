namespace NArk.Abstractions.Intents;

/// <summary>Lifecycle state of an Arkade intent.</summary>
public enum ArkIntentState
{
    /// <summary>Intent created locally but not yet submitted to the server.</summary>
    WaitingToSubmit,
    /// <summary>Submitted to the server; waiting for a batch to open.</summary>
    WaitingForBatch,
    /// <summary>Included in an active batch; MuSig2 signing in progress.</summary>
    BatchInProgress,
    /// <summary>The batch that included this intent failed; intent may be retried.</summary>
    BatchFailed,
    /// <summary>Batch committed on-chain; intent is settled.</summary>
    BatchSucceeded,
    /// <summary>Intent was cancelled before settlement.</summary>
    Cancelled
}
