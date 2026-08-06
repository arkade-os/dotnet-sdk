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
}
