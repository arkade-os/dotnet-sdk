using System.Numerics;

namespace NArk.Arkade.Scripts;

/// <summary>
/// Sign-magnitude little-endian encoder/decoder for ArkadeScript numeric pushes,
/// matching Bitcoin Script's standard <c>CScriptNum</c> serialization rules but
/// without the 4-byte (or 8-byte) range cap.
/// </summary>
/// <remarks>
/// <para>
/// ArkadeScript opcodes such as <see cref="ArkadeOpcode.OP_ECMULSCALARVERIFY"/>
/// and <see cref="ArkadeOpcode.OP_TWEAKVERIFY"/> push 32-byte EC scalars on the
/// stack — these don't fit in a 64-bit integer, so the existing internal
/// <c>NArk.Core.Helpers.CScriptNum</c> (which is <c>long</c>-backed) can't be
/// reused directly. This helper covers the wider range while staying byte-
/// compatible with <c>CScriptNum</c> for values that DO fit in a long.
/// </para>
/// <para>
/// Encoding rules (Bitcoin <c>CScriptNum::serialize</c>):
/// <list type="bullet">
///   <item>Zero is encoded as the empty byte array.</item>
///   <item>Non-zero values are written little-endian. The high bit of the most
///         significant byte is reserved for the sign — <c>1 = negative</c>,
///         <c>0 = positive</c>. If the value's natural top bit is already 1,
///         an extra byte is appended to carry the sign and avoid ambiguity.</item>
///   <item>Decoding is the reverse: an empty array decodes to zero; otherwise
///         the high bit of the last byte is the sign and the remaining bits
///         form the magnitude.</item>
/// </list>
/// </para>
/// <para>Mirrors the ts-sdk's reliance on <c>@scure/btc-signer</c>'s
/// <c>ScriptNum()</c> serializer; vectors must round-trip 1:1 across SDKs.</para>
/// </remarks>
public static class ArkadeScriptNum
{
    /// <summary>Encode a <see cref="BigInteger"/> using Bitcoin sign-magnitude LE.</summary>
    public static byte[] Encode(BigInteger value)
    {
        if (value.IsZero) return [];

        var negative = value.Sign < 0;
        var magnitude = negative ? -value : value;

        // BigInteger.ToByteArray returns two's-complement LE; we want unsigned LE
        // of the magnitude. Pass isUnsigned=true to drop the sign byte and
        // isBigEndian=false to get LE.
        var bytes = magnitude.ToByteArray(isUnsigned: true, isBigEndian: false);

        // If the top bit of the MSB is already set, we need an extra byte to
        // encode the sign without overflowing into the magnitude.
        if ((bytes[^1] & 0x80) != 0)
        {
            var widened = new byte[bytes.Length + 1];
            Array.Copy(bytes, widened, bytes.Length);
            widened[^1] = negative ? (byte)0x80 : (byte)0x00;
            return widened;
        }

        if (negative)
            bytes[^1] |= 0x80;
        return bytes;
    }

    /// <summary>
    /// Decode a Bitcoin sign-magnitude LE byte array to a <see cref="BigInteger"/>.
    /// Empty input decodes to zero. Throws if <paramref name="requireMinimal"/> is
    /// true and the encoding has a redundant zero byte / sign byte.
    /// </summary>
    public static BigInteger Decode(ReadOnlySpan<byte> bytes, bool requireMinimal = true)
    {
        if (bytes.Length == 0) return BigInteger.Zero;

        if (requireMinimal)
        {
            // The most-significant byte cannot be 0x00 or 0x80 (sign-only)
            // unless the second-most-significant byte's high bit is set —
            // otherwise the value should have been encoded one byte shorter.
            if ((bytes[^1] & 0x7F) == 0)
            {
                if (bytes.Length == 1 || (bytes[^2] & 0x80) == 0)
                    throw new ArgumentException(
                        "Non-minimally encoded script number.", nameof(bytes));
            }
        }

        var negative = (bytes[^1] & 0x80) != 0;

        // Strip the sign bit from the MSB so we can parse pure magnitude.
        Span<byte> magnitude = stackalloc byte[bytes.Length];
        bytes.CopyTo(magnitude);
        magnitude[^1] &= 0x7F;

        var value = new BigInteger(magnitude, isUnsigned: true, isBigEndian: false);
        return negative ? -value : value;
    }
}
