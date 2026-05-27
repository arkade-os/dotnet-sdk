using NBitcoin.DataEncoders;

namespace NArk.Swaps.Bolt12;

/// <summary>
/// Minimal parser for BOLT 12 invoice strings (<c>lni1…</c>).
/// Decodes the bech32m envelope and walks the TLV stream to extract
/// the fields we need for swap creation, without requiring a full
/// BOLT 12 library.
/// </summary>
internal static class Bolt12InvoiceParser
{
    // BOLT 12 invoice TLV field types (bolt12.md, "Invoice TLV Stream")
    private const ulong InvoicePaymentHashType = 168; // 0xA8

    private const string InvoiceHrpPrefix = "lni";
    private const string OfferHrpPrefix = "lno";

    /// <summary>
    /// Returns <c>true</c> when <paramref name="s"/> looks like a BOLT 12
    /// invoice (<c>lni1…</c>).
    /// </summary>
    public static bool IsBolt12Invoice(string s) =>
        !string.IsNullOrEmpty(s) &&
        s.StartsWith(InvoiceHrpPrefix + "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="s"/> looks like a BOLT 12
    /// offer (<c>lno1…</c>).
    /// </summary>
    public static bool IsBolt12Offer(string s) =>
        !string.IsNullOrEmpty(s) &&
        s.StartsWith(OfferHrpPrefix + "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the 32-byte SHA-256 payment hash from a BOLT 12 invoice string.
    /// The invoice must be bech32m-encoded and contain a TLV type-168 record.
    /// </summary>
    /// <param name="bolt12Invoice">
    /// A BOLT 12 invoice string with an <c>lni1</c> prefix.
    /// </param>
    /// <returns>32-byte payment hash.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="bolt12Invoice"/> is null or whitespace.
    /// </exception>
    /// <exception cref="FormatException">
    /// The string is not a valid BOLT 12 invoice, uses wrong bech32 variant,
    /// or does not contain a payment hash TLV record.
    /// </exception>
    public static byte[] ExtractPaymentHash(string bolt12Invoice)
    {
        if (string.IsNullOrWhiteSpace(bolt12Invoice))
            throw new ArgumentException("Invoice must not be empty.", nameof(bolt12Invoice));

        var lower = bolt12Invoice.ToLowerInvariant();

        if (!lower.StartsWith(InvoiceHrpPrefix + "1", StringComparison.Ordinal))
            throw new FormatException(
                $"Expected a BOLT 12 invoice (lni1…). " +
                $"Got: '{lower[..Math.Min(8, lower.Length)]}…'");

        var hrp = lower[..lower.IndexOf('1')];
        var encoder = Encoders.Bech32(hrp);
        encoder.StrictLength = false;
        encoder.SquashBytes = true;

        byte[] tlv;
        try
        {
            tlv = encoder.DecodeDataRaw(lower, out var encodingType);
            if (encodingType != Bech32EncodingType.BECH32M)
                throw new FormatException("BOLT 12 invoice must use bech32m encoding.");
        }
        catch (FormatException) { throw; }
        catch (Exception ex)
        {
            throw new FormatException("Failed to decode BOLT 12 invoice envelope.", ex);
        }

        var hash = FindTlvRecord(tlv, InvoicePaymentHashType);
        if (hash is null)
            throw new FormatException(
                $"Payment hash (TLV type {InvoicePaymentHashType}) not found in BOLT 12 invoice.");

        if (hash.Length != 32)
            throw new FormatException(
                $"Payment hash TLV record has unexpected length {hash.Length} (expected 32).");

        return hash;
    }

    /// <summary>
    /// Scans a TLV byte stream and returns the value bytes of the first record
    /// matching <paramref name="targetType"/>, or <c>null</c> if not found.
    /// Records are encoded as [type: BigSize][length: BigSize][value: bytes].
    /// </summary>
    internal static byte[]? FindTlvRecord(byte[] tlv, ulong targetType)
    {
        var pos = 0;
        while (pos < tlv.Length)
        {
            var type = ReadBigSize(tlv, ref pos);
            if (pos >= tlv.Length) break;
            var length = (int)ReadBigSize(tlv, ref pos);
            if (pos + length > tlv.Length) break;

            if (type == targetType)
                return tlv[pos..(pos + length)];

            pos += length;
        }
        return null;
    }

    /// <summary>
    /// Reads one Lightning BigSize-encoded <c>ulong</c> from <paramref name="data"/>
    /// at <paramref name="pos"/> and advances <paramref name="pos"/> past it.
    /// Encoding: 1 byte if value ≤ 0xFC, 3 bytes (0xFD + uint16-BE) for ≤ 0xFFFF,
    /// 5 bytes (0xFE + uint32-BE) for ≤ 0xFFFFFFFF, 9 bytes (0xFF + uint64-BE) otherwise.
    /// </summary>
    internal static ulong ReadBigSize(byte[] data, ref int pos)
    {
        var b = data[pos++];
        return b switch
        {
            <= 0xFC => b,
            0xFD => (ulong)data[pos++] << 8 | data[pos++],
            0xFE => (ulong)data[pos++] << 24 | (ulong)data[pos++] << 16
                    | (ulong)data[pos++] << 8 | data[pos++],
            _ => (ulong)data[pos++] << 56 | (ulong)data[pos++] << 48
                 | (ulong)data[pos++] << 40 | (ulong)data[pos++] << 32
                 | (ulong)data[pos++] << 24 | (ulong)data[pos++] << 16
                 | (ulong)data[pos++] << 8 | data[pos++]
        };
    }
}
