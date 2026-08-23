namespace NArk.Abstractions.Batches;

/// <summary>
/// Submits signed forfeit transactions to the server, optionally alongside the signed commitment tx.
/// </summary>
/// <param name="SignedForfeitTxs">Fully signed forfeit transactions (hex).</param>
/// <param name="SignedCommitmentTx">Signed commitment tx (hex), required when the client is a cosigner.</param>
public record SubmitSignedForfeitTxsRequest(string[] SignedForfeitTxs, string? SignedCommitmentTx = null);
