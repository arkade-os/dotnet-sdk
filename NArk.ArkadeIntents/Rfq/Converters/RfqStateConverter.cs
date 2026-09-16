using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.Rfq.Converters;

/// <summary>
/// Maps <see cref="RfqState"/> to and from its wire strings. An unrecognised state degrades to
/// <see cref="RfqState.Unknown"/>, which is deliberately non-terminal: better to keep watching the
/// settlement on-chain than to stop on a word we do not know.
/// </summary>
public sealed class RfqStateConverter : JsonConverter<RfqState>
{
    /// <inheritdoc />
    public override RfqState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "refused" => RfqState.Refused,
            "quoted" => RfqState.Quoted,
            "expired" => RfqState.Expired,
            "funded" => RfqState.Funded,
            "filling" => RfqState.Filling,
            "filled" => RfqState.Filled,
            "settled" => RfqState.Settled,
            "refunded" => RfqState.Refunded,
            "stuck" => RfqState.Stuck,
            _ => RfqState.Unknown,
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RfqState value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
