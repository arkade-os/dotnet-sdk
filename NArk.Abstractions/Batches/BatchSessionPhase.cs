namespace NArk.Abstractions.Batches;

/// <summary>
/// Phases a batch session moves through, in the order the operator drives them.
/// Each event is only acted on in the phase(s) that can legitimately produce it.
/// </summary>
public enum BatchSessionPhase
{
    Started,
    TreeSigningStarted,
    TreeNoncesAggregated,
    Finalizing
}