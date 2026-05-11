namespace NArk.Core.Services;

/// <summary>
/// Raised by <see cref="PendingArkTransactionRecoveryService"/> when finalizing a
/// single pending Arkade transaction fails. Subscribers can use this to surface a
/// non-blocking banner in the wallet UI, ship telemetry, or schedule a manual retry —
/// the recovery service itself moves on to the next pending tx in the same batch
/// regardless, so failures here never block start-up.
/// </summary>
/// <remarks>
/// The event fires <em>after</em> the failure has been logged at warning level, and
/// <em>before</em> the recovery loop continues with the next pending tx. Subscribers
/// must not throw — exceptions raised inside handlers are observed but not surfaced
/// (treat the event as a fire-and-forget signal).
/// </remarks>
public sealed class PendingTxRecoveryFailureEventArgs : EventArgs
{
    public PendingTxRecoveryFailureEventArgs(string walletId, string arkTxId, Exception exception)
    {
        WalletId = walletId;
        ArkTxId = arkTxId;
        Exception = exception;
    }

    /// <summary>Wallet whose pending tx failed to finalize.</summary>
    public string WalletId { get; }

    /// <summary>Server-assigned id of the pending Arkade transaction.</summary>
    public string ArkTxId { get; }

    /// <summary>The exception that aborted the finalize attempt.</summary>
    public Exception Exception { get; }
}
