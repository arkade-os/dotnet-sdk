using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// RFQ v1 — the request-for-quote negotiation layer shared by every Arkade swap corridor
/// (Lightning, cross-chain and arkade-to-arkade alike).
/// </summary>
/// <remarks>
/// <para>
/// The envelope and the message family are corridor-agnostic; everything corridor-specific lives in
/// the <c>profile</c> object each message carries, which is why the messages are generic over it.
/// A transport maps these payloads to its own framing but never reshapes them, so the same bytes go
/// over HTTP or a relay.
/// </para>
/// <para>
/// Two asymmetric rules are load-bearing and are encoded rather than described. <b>Requests are
/// strict</b>: a solver rejects unknown fields with <see cref="RfqRefusalReason.UnsupportedPayload"/>,
/// so a request serializes exactly its declared fields. <b>Responses are tolerant</b>: unknown
/// members are ignored, which is what lets solvers extend responses without a version bump.
/// </para>
/// </remarks>
public static class RfqProtocol
{
    /// <summary>The protocol version this client speaks.</summary>
    public const int Version = 1;

    /// <summary>
    /// Serializer settings for the whole family: snake_case names, and unknown members ignored on
    /// the way in — the "responses are tolerant" rule.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// A fresh client-chosen negotiation id: 32 random bytes, lowercase hex. It is the idempotency
    /// and correlation key for the whole negotiation, so it must be unpredictable per swap.
    /// </summary>
    /// <returns>64 lowercase hex characters.</returns>
    public static string NewRfqId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Validate a reply and narrow it to the quote we asked for. A refusal throws; so does a quote
    /// for a different negotiation, or for a different market.
    /// </summary>
    /// <typeparam name="TQuoteProfile">The corridor's quote-profile shape.</typeparam>
    /// <param name="payload">The reply payload.</param>
    /// <param name="rfqId">The correlation id of the request.</param>
    /// <param name="requestedPair">
    /// The pair that was asked for, or <c>null</c> to accept whatever the quote names.
    /// </param>
    /// <returns>The quote.</returns>
    /// <exception cref="RfqRefusedException">The reply was a refusal.</exception>
    /// <exception cref="InvalidOperationException">The reply was not a quote for this negotiation.</exception>
    /// <remarks>
    /// <para>
    /// Matching the correlation id is what stops a stale or misrouted reply being funded: on a
    /// shared relay every one of the solver's events arrives on the same subscription.
    /// </para>
    /// <para>
    /// The pair is compared byte for byte, the way the solver compares it, because a solver that
    /// normalises case or quotes a market other than the one asked for is otherwise undetectable
    /// from here. A quote's pair is a constant the solver restates rather than the request's echoed
    /// back, so this binds every solver to the exact spellings the profiles build.
    /// </para>
    /// </remarks>
    public static RfqQuote<TQuoteProfile> ExpectQuote<TQuoteProfile>(
        JsonNode payload, string rfqId, string? requestedPair = null)
    {
        if (TypeOf(payload) == "rfq_refusal")
        {
            var refusal = payload.Deserialize<RfqRefusal>(RfqProtocol.Json)!;
            throw new RfqRefusedException(refusal.Reason, refusal.RfqId ?? rfqId, refusal.Detail);
        }

        if (TypeOf(payload) != "rfq_quote")
        {
            throw new InvalidOperationException($"unexpected reply type '{TypeOf(payload) ?? "(none)"}'");
        }

        var quote = payload.Deserialize<RfqQuote<TQuoteProfile>>(RfqProtocol.Json)!;
        if (quote.RfqId != rfqId)
        {
            throw new InvalidOperationException(
                $"quote answers negotiation '{quote.RfqId}', not '{rfqId}'");
        }
        if (requestedPair is not null && quote.Pair != requestedPair)
        {
            throw new InvalidOperationException(
                $"quote is for market '{quote.Pair ?? "(none)"}', not the requested '{requestedPair}'");
        }
        return quote;
    }



    /// <summary>The payload's discriminator, or null when it carries none.</summary>
    private static string? TypeOf(System.Text.Json.Nodes.JsonNode? payload) =>
        payload?["type"]?.GetValue<string>();
}
