namespace NArk.Core.Services;

/// <summary>
/// Thrown by <see cref="PendingArkTransactionRecoveryService"/> when a pending Arkade
/// transaction reported by the server fails local authorization, i.e. it is not a
/// transaction this wallet ever submitted.
/// </summary>
/// <remarks>
/// <para>
/// Pending-tx recovery signs checkpoint PSBTs that arrive entirely from the server, so
/// every checkpoint is re-derived locally before a signature is produced: the checkpoint
/// must pay the spent input's full value into the checkpoint contract this wallet would
/// itself have built, and the accompanying final Arkade transaction must be the one the
/// wallet signed at submit time. This exception means one of those checks failed, so the
/// transaction is outside what the wallet ever authorized and is left unsigned.
/// </para>
/// <para>
/// It is raised per pending transaction: the recovery loop logs it, surfaces it on
/// <see cref="PendingArkTransactionRecoveryService.RecoveryFailed"/>, and continues with
/// the next pending transaction. No signature has been produced for the rejected
/// transaction, and none will be on subsequent runs while it still fails validation.
/// </para>
/// </remarks>
public sealed class UnauthorizedPendingArkTransactionException : Exception
{
    /// <summary>Creates the exception for a rejected pending Arkade transaction.</summary>
    /// <param name="arkTxId">Server-advertised id of the rejected pending transaction.</param>
    /// <param name="reason">Why the transaction was rejected.</param>
    public UnauthorizedPendingArkTransactionException(string arkTxId, string reason)
        : base($"Refusing to sign pending Arkade transaction {arkTxId}: {reason}")
    {
        ArkTxId = arkTxId;
        Reason = reason;
    }

    /// <summary>Server-advertised id of the rejected pending Arkade transaction.</summary>
    public string ArkTxId { get; }

    /// <summary>Why the transaction was rejected, without the message prefix.</summary>
    public string Reason { get; }
}
