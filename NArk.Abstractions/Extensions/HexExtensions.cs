namespace NArk.Core.Extensions;

/// <summary>
/// Hex conversion extension methods for .NET 8 compatibility.
/// In .NET 9+, use Convert.ToHexStringLower directly.
/// </summary>
public static class HexExtensions
{
    /// <summary>
    /// Converts a byte array to a lowercase hexadecimal string.
    /// </summary>
    public static string ToHexStringLower(this byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Converts a ReadOnlySpan of bytes to a lowercase hexadecimal string.
    /// </summary>
    public static string ToHexStringLower(this ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
