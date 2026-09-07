namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// Carries the RFQ message family to a solver. The payloads are identical whatever the transport —
/// only the framing differs — so a corridor's client is written once against this seam.
/// </summary>
/// <remarks>
/// Today's implementation is <see cref="HttpRfqTransport"/>. A relay transport (both parties
/// outbound, addressed by pubkey) is the other shape the protocol defines; it is the only way to
/// reach a solver deployed with no listening ports, and the only way to use the solver registry's
/// rendezvous data, whose corridor cards carry a discovery pubkey and relays rather than a URL.
/// </remarks>
public interface IRfqTransport
{
    /// <summary>
    /// Ask for a quote and return the solver's binding terms.
    /// </summary>
    /// <typeparam name="TRequestProfile">The corridor's request-profile shape.</typeparam>
    /// <typeparam name="TQuoteProfile">The corridor's quote-profile shape.</typeparam>
    /// <param name="request">The request payload; strict, so it must carry no extra fields.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    /// <returns>The solver's quote for <paramref name="request"/>.</returns>
    /// <exception cref="RfqRefusedException">The solver declined, with a reason from the closed set.</exception>
    /// <exception cref="InvalidOperationException">The reply was neither a quote nor a refusal, or answered a different negotiation.</exception>
    Task<RfqQuote<TQuoteProfile>> RequestQuoteAsync<TRequestProfile, TQuoteProfile>(
        RfqRequest<TRequestProfile> request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask the solver where a negotiation stands.
    /// </summary>
    /// <typeparam name="TStatusProfile">The corridor's status-profile shape.</typeparam>
    /// <param name="rfqId">The correlation id.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    /// <returns>The status, or <c>null</c> when the solver has no negotiation under that id.</returns>
    /// <remarks>
    /// Best-effort by design — never the money path. A funded swap is observable on-chain whether
    /// or not the solver answers.
    /// </remarks>
    Task<RfqStatus<TStatusProfile>?> GetStatusAsync<TStatusProfile>(
        string rfqId,
        CancellationToken cancellationToken = default);
}
