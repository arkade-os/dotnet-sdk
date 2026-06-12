namespace NArk.Core.Recovery;

/// <summary>
/// Idempotently ensures a SingleKey wallet's CURRENT-signer "Default" contract exists
/// (Active, <c>Metadata["Source"] == "Default"</c>) and reports its script.
/// <para>
/// Extracted from <see cref="SingleKeyVtxoRecoveryService"/> so the
/// reconciliation service can depend on (and substitute) a minimal seam rather
/// than the concrete recovery service.
/// </para>
/// </summary>
public interface ISingleKeyDefaultEnsurer
{
    /// <summary>
    /// Ensures the wallet's current-signer Default contract exists and returns its script hex.
    /// </summary>
    Task<string> EnsureDefaultAsync(string walletId, CancellationToken cancellationToken = default);
}
