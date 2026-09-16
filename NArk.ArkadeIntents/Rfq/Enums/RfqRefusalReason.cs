using System.Text.Json.Serialization;
using NArk.ArkadeIntents.Rfq.Converters;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// The closed refusal set of RFQ v1. Anything outside it deserializes to <see cref="Unknown"/> —
/// clients must treat an unrecognised reason as a generic decline and infer no retry semantics
/// from it.
/// </summary>
[JsonConverter(typeof(RfqRefusalReasonConverter))]
public enum RfqRefusalReason
{
    /// <summary>A reason outside the closed set: a generic decline.</summary>
    Unknown,

    /// <summary>Pair not served (includes the wrong network for the asset).</summary>
    UnsupportedPair,

    /// <summary>Malformed request, unknown fields, or a profile input the solver cannot serve.</summary>
    UnsupportedPayload,

    /// <summary>Outside the solver's min/max for the pair.</summary>
    AmountOutOfRange,

    /// <summary>The solver is at aggregate capacity right now.</summary>
    ExposureCap,

    /// <summary>The supplied invoice is expired, or expires too soon to swap safely.</summary>
    InvoiceExpired,

    /// <summary>The id or natural key is already bound to a different or already-progressed negotiation.</summary>
    QuoteConflict,

    /// <summary>The solver cannot price the pair right now (no market data).</summary>
    PricingUnavailable,

    /// <summary>
    /// Too many quotes asked for from one identity inside a lockup window.
    /// </summary>
    /// <remarks>
    /// The solver meters quote creation per requester — socket address over HTTP, author key on a
    /// relay — so that squatting its exposure cap costs a distributed effort. Worth telling apart
    /// from the other refusals because it is the only one that a caller fixes by waiting, or by not
    /// reusing one identity across every negotiation.
    /// </remarks>
    RateLimited,
}
