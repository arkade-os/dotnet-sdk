using System.Text.RegularExpressions;

namespace NArk.ArkadeIntents.Rfq.Profiles.Evm;

internal static class EvmWire
{
    private static readonly Regex AddressPattern = new(
        "\\A0x[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Hex32Pattern = new(
        "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);

    internal static string RequireAddress(string? value, string name, bool lowerCase = false)
    {
        if (value is null || !AddressPattern.IsMatch(value) || lowerCase && value != value.ToLowerInvariant())
            throw new ArgumentException("expected 0x followed by 40 hexadecimal characters", name);
        return value;
    }

    internal static string RequireNonZeroAddress(string? value, string name, bool lowerCase = false)
    {
        var address = RequireAddress(value, name, lowerCase);
        if (address[2..].All(c => c == '0'))
            throw new ArgumentException("zero address is not allowed", name);
        return address;
    }

    internal static string RequireHex32(string? value, string name)
    {
        if (value is null || !Hex32Pattern.IsMatch(value))
            throw new ArgumentException("expected 64 lowercase hexadecimal characters", name);
        return value;
    }

    internal static string RequireNonZeroHex32(string? value, string name)
    {
        var hex = RequireHex32(value, name);
        if (hex.All(c => c == '0'))
            throw new ArgumentException("zero 32-byte value is not allowed", name);
        return hex;
    }
}
