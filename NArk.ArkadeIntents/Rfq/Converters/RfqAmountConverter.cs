using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.Rfq.Converters;

/// <summary>
/// Reads and writes an amount the way RFQ v1 § 2.1 states it: atomic units of one named asset, as a
/// canonical decimal string.
/// </summary>
/// <remarks>
/// <para>
/// The encoding is a string because JSON numbers are IEEE-754 doubles in every mainstream parser,
/// exact only to 2^53 − 1. For an 18-decimal asset that ceiling is 0.009 tokens, so a quote for one
/// whole token would be rounded inside the counterparty's <c>JSON.parse</c> before any validator on
/// either side could see it — and neither side could detect that it happened. Sats never come near
/// that ceiling; the encoding is shared across every asset the protocol admits, so this client
/// speaks it whatever it is trading.
/// </para>
/// <para>
/// Reading accepts a JSON number as well, which § 2.1 keeps for <c>v: 1</c> compatibility and which
/// is what solvers still emit. That tolerance is bounded exactly where the counterparty bounds it:
/// only a value a double carries exactly. A number past that ceiling is refused rather than read,
/// because the sender cannot have produced it reliably and this is the funding path — reading it
/// would mean funding a figure neither side can prove was the one quoted.
/// </para>
/// <para>
/// Distinct from the registry's <c>NumericStringConverter</c>, which is deliberately looser: a
/// hand-written market card is a browsing aid, while these are the amounts money moves by.
/// </para>
/// </remarks>
public sealed class RfqAmountConverter : JsonConverter<long>
{
    /// <inheritdoc />
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var raw = reader.GetString();
                if (!IsCanonicalDecimal(raw))
                {
                    throw new JsonException(
                        $"expected an amount as a canonical decimal string of atomic units, got \"{raw}\"");
                }

                // Canonical and digits-only, so the only way this fails is genuine overflow.
                if (!long.TryParse(raw, out var parsed))
                {
                    throw new JsonException($"the amount \"{raw}\" is larger than this client can represent");
                }

                return parsed;

            // § 2.1's v:1 allowance. Bounded to what a double carries exactly, matching the bound the
            // counterparty applies on the way in, so both sides agree on precisely which numbers are
            // legible rather than each guessing.
            case JsonTokenType.Number when reader.TryGetInt64(out var number):
                if (Math.Abs(number) > MaxSafeInteger)
                {
                    throw new JsonException(
                        $"the amount {number} is outside the range a JSON number carries exactly " +
                        $"(±{MaxSafeInteger}); it must be sent as a decimal string");
                }

                return number;

            case JsonTokenType.Number:
                throw new JsonException("expected an integer amount in atomic units, got a fractional number");

            default:
                throw new JsonException($"expected an amount, got {reader.TokenType}");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        // Invariant by construction: a long renders as ASCII digits with no separators, and the
        // negative case cannot reach the wire because no amount field is ever negative.
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>The largest integer a JSON number carries exactly — JavaScript's MAX_SAFE_INTEGER.</summary>
    private const long MaxSafeInteger = 9007199254740991L;

    /// <summary>
    /// § 2.1's canonical form: ASCII digits, no sign, no point, no exponent, and no leading zero
    /// unless the value is exactly <c>"0"</c>.
    /// </summary>
    /// <remarks>
    /// Checked rather than left to <see cref="long.TryParse(string, out long)"/>, which accepts
    /// leading whitespace, a sign and — under some cultures — group separators. Exponent notation is
    /// the one worth naming: <c>1e-8</c> and <c>1E8</c> are spellings a sender might reach for, and
    /// misreading either misprices by eight orders of magnitude.
    /// </remarks>
    private static bool IsCanonicalDecimal(string? value)
    {
        if (value is not { Length: > 0 }) return false;
        if (value.Length > 1 && value[0] == '0') return false;

        foreach (var c in value)
        {
            if (c is < '0' or > '9') return false;
        }

        return true;
    }
}
