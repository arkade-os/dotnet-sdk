using Microsoft.Extensions.Logging;
using NArk.Abstractions.Services;
using NArk.Core.Transformers;

namespace NArk.Core.Services;

/// <summary>
/// Orchestrates VTXO delegation to an external delegator service (e.g. Fulmine).
/// The client creates a partially signed intent and forfeit transactions,
/// then sends them to the delegator which registers the intent and participates
/// in batches on the owner's behalf before VTXOs expire.
/// </summary>
public class DelegationService(
    IEnumerable<IDelegationTransformer> transformers,
    IDelegatorProvider delegatorProvider,
    ILogger<DelegationService>? logger = null)
{
    /// <summary>
    /// Returns information about the delegator service including its public key.
    /// Use the pubkey when constructing <see cref="NArk.Core.Contracts.ArkDelegateContract"/> scripts.
    /// </summary>
    public Task<DelegatorInfo> GetDelegatorInfoAsync(CancellationToken cancellationToken = default)
        => delegatorProvider.GetDelegatorInfoAsync(cancellationToken);

    /// <summary>
    /// Returns the registered delegation transformers for checking contract eligibility.
    /// </summary>
    public IEnumerable<IDelegationTransformer> Transformers => transformers;

    /// <summary>
    /// Delegates the refresh of VTXOs by sending a partially signed intent and forfeit transactions
    /// to the delegator service. The delegator will register the intent and join batches
    /// on behalf of the owner when the VTXOs approach expiration.
    /// </summary>
    /// <param name="intentMessage">The intent message in plain-text (stringified JSON).</param>
    /// <param name="intentProof">The intent proof tx (PSBT in base64 format).</param>
    /// <param name="forfeitTxs">Partially signed forfeit transactions.</param>
    /// <param name="rejectReplace">If true, the delegator will not replace an existing delegation
    /// that includes at least one VTXO from this request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DelegateAsync(
        string intentMessage,
        string intentProof,
        string[] forfeitTxs,
        bool rejectReplace = false,
        CancellationToken cancellationToken = default)
    {
        await delegatorProvider.DelegateAsync(intentMessage, intentProof, forfeitTxs, rejectReplace, cancellationToken);
        logger?.LogInformation("Delegated intent to delegator service");
    }
}
