namespace NArk.Abstractions.Services;

/// <summary>
/// Provides communication with a delegator service (e.g. Fulmine)
/// for automatic VTXO rollover. The client creates a partially signed intent
/// and forfeit transactions, then sends them to the delegator which registers
/// the intent and participates in batches on the owner's behalf.
/// </summary>
public interface IDelegatorProvider
{
    /// <summary>
    /// Returns information about the delegator including its public key, fee, and address.
    /// Use the pubkey when constructing delegate contract scripts.
    /// </summary>
    Task<DelegatorInfo> GetDelegatorInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delegates the refresh of VTXOs by sending a partially signed intent and forfeit transactions.
    /// The delegator will register the intent and participate in batches on behalf of the owner.
    /// </summary>
    /// <param name="intentMessage">The intent message in plain-text (stringified JSON).</param>
    /// <param name="intentProof">The intent proof tx (PSBT in base64 format).</param>
    /// <param name="forfeitTxs">Partially signed forfeit transactions (hex or base64).</param>
    /// <param name="rejectReplace">If true, the delegator will not replace an existing delegation
    /// that includes at least one VTXO from this request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DelegateAsync(
        string intentMessage,
        string intentProof,
        string[] forfeitTxs,
        bool rejectReplace = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a delegator service.
/// </summary>
/// <param name="Pubkey">The delegator's compressed public key (hex-encoded).</param>
/// <param name="Fee">The service fee applied by the delegator.</param>
/// <param name="DelegatorAddress">The delegator's Ark address for receiving service fees.</param>
public record DelegatorInfo(string Pubkey, string Fee, string DelegatorAddress);
