namespace NArk.Core;

/// <summary>
/// Client-side guardrails mirroring the Arkade server's transaction limits.
/// </summary>
public static class ArkTransactionLimits
{
    /// <summary>
    /// Maximum number of VTXO inputs to include in a single Arkade transaction
    /// or intent. The Arkade server rejects transactions and intent proofs whose
    /// weight exceeds its configured <c>max_tx_weight</c> (error code
    /// <c>TX_TOO_LARGE</c>); this conservative input cap keeps generated
    /// transactions under the default limit.
    /// </summary>
    /// <remarks>
    /// TODO: replace the fixed cap with a real weight estimate driven by the
    /// server's advertised <c>max_tx_weight</c> (available via GetInfo).
    /// </remarks>
    public const int MaxVtxosPerArkTransaction = 50;
}
