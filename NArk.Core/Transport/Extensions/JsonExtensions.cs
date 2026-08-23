using System.Text.Json;

namespace NArk.Core.Extensions;

/// <summary>
/// Helpers for reading proto3 JSON that may arrive under either the snake_case
/// proto field name or its camelCase JSON name.
/// </summary>
public static class JsonExtensions
{
    /// <summary>
    /// Tries to read a property by its snake_case name, falling back to the
    /// camelCase (proto3 JSON) spelling when the snake_case key is absent.
    /// </summary>
    /// <param name="el">The element to read from.</param>
    /// <param name="snakeCaseKey">The proto field name in snake_case (e.g. <c>ark_txid</c>).</param>
    /// <param name="value">The matched property, or <c>default</c> when neither spelling exists.</param>
    /// <returns><c>true</c> if either spelling was found.</returns>
    public static bool TryGetPropInvariantCase(this JsonElement el, string snakeCaseKey, out JsonElement value)
    {
        return el.TryGetProperty(snakeCaseKey, out value)
               || el.TryGetProperty(ToCamelCase(snakeCaseKey), out value);
    }

    /// <summary>
    /// Reads a property by its snake_case name, falling back to the camelCase
    /// (proto3 JSON) spelling. Throws <see cref="KeyNotFoundException"/> if neither exists.
    /// </summary>
    /// <param name="el">The element to read from.</param>
    /// <param name="snakeCaseKey">The proto field name in snake_case (e.g. <c>ark_txid</c>).</param>
    /// <returns>The matched property.</returns>
    public static JsonElement GetPropInvariantCase(this JsonElement el, string snakeCaseKey)
    {
        if (el.TryGetProperty(snakeCaseKey, out var v)) return v;
        return el.GetProperty(ToCamelCase(snakeCaseKey));
    }
    
    private static string ToCamelCase(string snakeCase)
    {
        if (snakeCase.IndexOf('_') < 0)
            return snakeCase;

        Span<char> buf = stackalloc char[snakeCase.Length];
        var j = 0;
        var upperNext = false;
        foreach (var c in snakeCase)
        {
            if (c == '_')
            {
                upperNext = true;
                continue;
            }
            // Shift a-z only — matches protobuf's ToUpperASCII and leaves digits and
            // already-upper acronym chars untouched.
            buf[j++] = upperNext && c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
            upperNext = false;
        }
        return new string(buf[..j]);
    }
}


