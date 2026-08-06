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
}
