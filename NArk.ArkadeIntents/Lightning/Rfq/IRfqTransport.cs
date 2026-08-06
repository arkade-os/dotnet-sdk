namespace NArk.ArkadeIntents.Lightning.Rfq;

/// <summary>
/// Carries the RFQ message family to a solver. The payloads are identical whatever the transport —
/// only the framing differs — so the swap client is written once against this seam.
/// </summary>
/// <remarks>
/// Today's implementation is <see cref="HttpRfqTransport"/>. A relay transport (both parties
/// outbound, addressed by pubkey) is the other shape the spec defines; it is the only way to reach
/// a solver deployed with no listening ports, and the only way to use the solver registry's
/// rendezvous data, whose corridor cards carry a discovery pubkey and relays rather than a URL.
/// </remarks>
public interface IRfqTransport
{
    /// <summary>
    /// Ask for a quote and return the solver's binding terms.
    /// </summary>
    /// <param name="request">The request payload; strict, so it must carry no extra fields.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    /// <returns>The solver's quote for <paramref name="request"/>.</returns>
    /// <exception cref="RfqRefusedException">The solver declined, with a reason from the closed set.</exception>
    /// <exception cref="InvalidOperationException">The reply was neither a quote nor a refusal, or answered a different negotiation.</exception>
    Task<RfqQuote> RequestQuoteAsync(RfqRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask the solver where a negotiation stands.
    /// </summary>
    /// <param name="rfqId">The correlation id.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    /// <returns>The status, or <c>null</c> when the solver has no negotiation under that id.</returns>
    /// <remarks>
    /// Best-effort by design — never the money path. A funded swap is observable on-chain whether
    /// or not the solver answers, and the claim that spends the lockup carries the preimage.
    /// </remarks>
    Task<RfqStatus?> GetStatusAsync(string rfqId, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a solver declines a request.</summary>
public sealed class RfqRefusedException : Exception
{
    /// <summary>Creates the exception from a refusal payload.</summary>
    /// <param name="reason">The reason from the closed set; unrecognised wire values arrive as <see cref="RfqRefusalReason.Unknown"/>.</param>
    /// <param name="rfqId">The correlation id refused, when the solver echoed one.</param>
    /// <param name="detail">The solver's optional human-readable elaboration. Never branch on it.</param>
    public RfqRefusedException(RfqRefusalReason reason, string? rfqId = null, string? detail = null)
        : base($"solver refused: {reason}{(detail is null ? "" : $" ({detail})")}")
    {
        Reason = reason;
        RfqId = rfqId;
        Detail = detail;
    }

    /// <summary>Why the solver declined.</summary>
    public RfqRefusalReason Reason { get; }

    /// <summary>The correlation id refused, if the solver echoed one.</summary>
    public string? RfqId { get; }

    /// <summary>The solver's optional elaboration, for humans and logs only.</summary>
    public string? Detail { get; }
}
