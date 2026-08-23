namespace NArk.Abstractions.Batches.ServerEvents;

/// <summary>
/// Server is ready to finalize the batch and publish the unsigned commitment tx for cosigner review.
/// </summary>
/// <param name="CommitmentTx">Unsigned commitment transaction (hex).</param>
/// <param name="Id">Batch ID.</param>
public record BatchFinalizationEvent(string CommitmentTx, string Id) : BatchEvent;
