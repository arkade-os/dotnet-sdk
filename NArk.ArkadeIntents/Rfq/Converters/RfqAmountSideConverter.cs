using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.Rfq.Converters;

/// <summary>Maps <see cref="RfqAmountSide"/> to and from its lowercase wire strings.</summary>
public sealed class RfqAmountSideConverter : JsonConverter<RfqAmountSide>
{
    /// <inheritdoc />
    public override RfqAmountSide Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "from" => RfqAmountSide.From,
            "to" => RfqAmountSide.To,
            var other => throw new JsonException($"unknown amount_side '{other}'"),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RfqAmountSide value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == RfqAmountSide.From ? "from" : "to");
}
