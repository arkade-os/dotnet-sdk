using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.Rfq.Converters;

/// <summary>
/// Maps <see cref="RfqRefusalReason"/> to and from its wire strings, degrading anything outside the
/// closed set to <see cref="RfqRefusalReason.Unknown"/> rather than throwing — a solver that adds a
/// reason must not break a client that has not heard of it.
/// </summary>
public sealed class RfqRefusalReasonConverter : JsonConverter<RfqRefusalReason>
{
    /// <inheritdoc />
    public override RfqRefusalReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "unsupported_pair" => RfqRefusalReason.UnsupportedPair,
            "rate_limited" => RfqRefusalReason.RateLimited,
            "unsupported_payload" => RfqRefusalReason.UnsupportedPayload,
            "amount_out_of_range" => RfqRefusalReason.AmountOutOfRange,
            "exposure_cap" => RfqRefusalReason.ExposureCap,
            "invoice_expired" => RfqRefusalReason.InvoiceExpired,
            "quote_conflict" => RfqRefusalReason.QuoteConflict,
            "pricing_unavailable" => RfqRefusalReason.PricingUnavailable,
            _ => RfqRefusalReason.Unknown,
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RfqRefusalReason value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            RfqRefusalReason.UnsupportedPair => "unsupported_pair",
            RfqRefusalReason.RateLimited => "rate_limited",
            RfqRefusalReason.UnsupportedPayload => "unsupported_payload",
            RfqRefusalReason.AmountOutOfRange => "amount_out_of_range",
            RfqRefusalReason.ExposureCap => "exposure_cap",
            RfqRefusalReason.InvoiceExpired => "invoice_expired",
            RfqRefusalReason.QuoteConflict => "quote_conflict",
            RfqRefusalReason.PricingUnavailable => "pricing_unavailable",
            _ => "unknown",
        });
}
