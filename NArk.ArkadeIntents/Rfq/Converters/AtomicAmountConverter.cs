using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.Rfq.Converters;

/// <summary>Lossless, non-negative atomic amounts; strings have no integer-width limit and numbers must be safe integers.</summary>
public sealed class AtomicAmountConverter : JsonConverter<BigInteger>
{
    /// <inheritdoc />
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number)
            && number is >= 0 and <= 9007199254740991L)
            return number;
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } value
            && (value.Length == 1 || value[0] != '0') && value.All(c => c is >= '0' and <= '9'))
            return BigInteger.Parse(value, CultureInfo.InvariantCulture);
        throw new JsonException("Expected a canonical non-negative decimal string or a non-negative safe JSON integer.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
    {
        if (value.Sign < 0) throw new JsonException("An atomic amount cannot be negative.");
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
