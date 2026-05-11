namespace NArk.Core.Transport.Models;

/// <summary>
/// A pending Arkade transaction the server is holding open: the user submitted it via
/// <see cref="IClientTransport.SubmitTx"/> (which made the server lock the inputs as
/// "in-flight") but the matching <see cref="IClientTransport.FinalizeTx"/> never landed.
/// Returned by <see cref="IClientTransport.GetPendingTxAsync"/> so the SDK can sign the
/// checkpoint PSBTs and finalize on the next startup, releasing the user's coins.
/// </summary>
public record PendingArkTransaction(string ArkTxId, string FinalArkTx, string[] SignedCheckpointTxs);
