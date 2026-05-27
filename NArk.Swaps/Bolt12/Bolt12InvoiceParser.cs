namespace NArk.Swaps.Bolt12;

/// <summary>
/// Minimal parser for BOLT 12 invoice strings (<c>lni1…</c>) and offer strings (<c>lno1…</c>).
/// Decodes the bech32-without-checksum envelope and walks the TLV stream to extract
/// the fields needed for swap creation, without requiring a full BOLT 12 library.
/// </summary>
/// <remarks>
/// BOLT 12 encoding uses bech32 <em>without</em> a checksum — unlike segwit addresses
/// (bech32) or Taproot (bech32m), there are no checksum bytes appended. See
/// https://github.com/lightning/bolts/blob/master/12-offer-encoding.md §3.
///
/// <b>Verification:</b> <see cref="VerifyInvoiceMatchesOffer"/> checks that
/// <c>invoice_node_id</c> (TLV 176) equals <c>offer_issuer_id</c> (TLV 22), as required
/// by the BOLT 12 spec. This covers offers with an explicit issuer key. Offers that
/// expose only blinded paths (no <c>offer_issuer_id</c>) cannot be verified with a pubkey
/// comparison alone; full Merkle-root verification would be needed for those.
/// </remarks>
internal static class Bolt12InvoiceParser
{
    // BOLT 12 TLV field types (bolt12.md).
    private const ulong InvoicePaymentHashType = 168; // 0xA8 — invoice_payment_hash
    private const ulong InvoiceNodeIdType       = 176; // 0xB0 — invoice_node_id
    private const ulong OfferIssuerIdType       =  22; // 0x16 — offer_issuer_id

    private const string InvoiceHrpPrefix = "lni";
    private const string OfferHrpPrefix = "lno";

    // Standard bech32 alphabet (same for bech32, bech32m, and no-checksum).
    private const string Bech32Alphabet = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static readonly byte[] Bech32CharMap = BuildCharMap();

    private static byte[] BuildCharMap()
    {
        var map = new byte[128];
        Array.Fill(map, (byte)255);
        for (var i = 0; i < Bech32Alphabet.Length; i++)
            map[(int)Bech32Alphabet[i]] = (byte)i;
        return map;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="s"/> looks like a BOLT 12
    /// invoice (<c>lni1…</c>). Network-agnostic: both mainnet and testnet
    /// invoices use the same <c>lni</c> HRP.
    /// </summary>
    public static bool IsBolt12Invoice(string s) =>
        !string.IsNullOrEmpty(s) &&
        s.StartsWith(InvoiceHrpPrefix + "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="s"/> looks like a BOLT 12
    /// offer (<c>lno1…</c>). Network-agnostic: both mainnet and testnet
    /// offers use the same <c>lno</c> HRP.
    /// </summary>
    public static bool IsBolt12Offer(string s) =>
        !string.IsNullOrEmpty(s) &&
        s.StartsWith(OfferHrpPrefix + "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the 32-byte SHA-256 payment hash from a BOLT 12 invoice string.
    /// The invoice must be bech32-without-checksum encoded and contain a TLV
    /// type-168 record.
    /// </summary>
    /// <param name="bolt12Invoice">
    /// A BOLT 12 invoice string with an <c>lni1</c> prefix.
    /// </param>
    /// <returns>32-byte payment hash.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="bolt12Invoice"/> is null or whitespace.
    /// </exception>
    /// <exception cref="FormatException">
    /// The string is not a valid BOLT 12 invoice or does not contain a
    /// payment hash TLV record.
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

        byte[] tlv;
        try
        {
            tlv = DecodeBolt12Bech32(lower);
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
    /// Extracts the 33-byte compressed public key from the <c>invoice_node_id</c>
    /// field (TLV type 176) of a BOLT 12 invoice.
    /// </summary>
    /// <param name="bolt12Invoice">A BOLT 12 invoice string with an <c>lni1</c> prefix.</param>
    /// <returns>33-byte compressed public key.</returns>
    /// <exception cref="FormatException">
    /// The string is not a valid BOLT 12 invoice or does not contain <c>invoice_node_id</c>.
    /// </exception>
    public static byte[] ExtractNodeId(string bolt12Invoice)
    {
        if (string.IsNullOrWhiteSpace(bolt12Invoice))
            throw new ArgumentException("Invoice must not be empty.", nameof(bolt12Invoice));

        var lower = bolt12Invoice.ToLowerInvariant();
        if (!lower.StartsWith(InvoiceHrpPrefix + "1", StringComparison.Ordinal))
            throw new FormatException(
                $"Expected a BOLT 12 invoice (lni1…). Got: '{lower[..Math.Min(8, lower.Length)]}…'");

        var tlv = DecodeBolt12Bech32(lower);
        var nodeId = FindTlvRecord(tlv, InvoiceNodeIdType);

        if (nodeId is null)
            throw new FormatException(
                $"invoice_node_id (TLV type {InvoiceNodeIdType}) not found in BOLT 12 invoice.");
        if (nodeId.Length != 33)
            throw new FormatException(
                $"invoice_node_id TLV record has unexpected length {nodeId.Length} (expected 33).");

        return nodeId;
    }

    /// <summary>
    /// Extracts the 33-byte compressed public key from the <c>offer_issuer_id</c>
    /// field (TLV type 22) of a BOLT 12 offer.
    /// Returns <c>null</c> when the offer uses only blinded paths and carries no
    /// explicit issuer key.
    /// </summary>
    /// <param name="bolt12Offer">A BOLT 12 offer string with an <c>lno1</c> prefix.</param>
    /// <returns>33-byte compressed public key, or <c>null</c>.</returns>
    /// <exception cref="FormatException">
    /// The string is not a valid BOLT 12 offer, or the <c>offer_issuer_id</c> field
    /// has an unexpected length.
    /// </exception>
    public static byte[]? ExtractOfferIssuerId(string bolt12Offer)
    {
        if (string.IsNullOrWhiteSpace(bolt12Offer))
            throw new ArgumentException("Offer must not be empty.", nameof(bolt12Offer));

        var lower = bolt12Offer.ToLowerInvariant();
        if (!lower.StartsWith(OfferHrpPrefix + "1", StringComparison.Ordinal))
            throw new FormatException(
                $"Expected a BOLT 12 offer (lno1…). Got: '{lower[..Math.Min(8, lower.Length)]}…'");

        var tlv = DecodeBolt12Bech32(lower);
        var issuerId = FindTlvRecord(tlv, OfferIssuerIdType);

        if (issuerId is null) return null; // blinded-path-only offer
        if (issuerId.Length != 33)
            throw new FormatException(
                $"offer_issuer_id TLV record has unexpected length {issuerId.Length} (expected 33).");

        return issuerId;
    }

    /// <summary>
    /// Verifies that <paramref name="bolt12Invoice"/> was issued for
    /// <paramref name="bolt12Offer"/> by asserting that
    /// <c>invoice_node_id</c> (TLV 176) equals <c>offer_issuer_id</c> (TLV 22).
    /// </summary>
    /// <remarks>
    /// Per BOLT 12 §Invoice: "if <c>offer_issuer_id</c> is present, MUST set
    /// <c>invoice_node_id</c> to <c>offer_issuer_id</c>." Callers MUST reject the
    /// invoice when this check fails — the invoice may have been substituted by
    /// a man-in-the-middle (e.g. Boltz acting as the offer resolver).
    ///
    /// When the offer carries no explicit <c>offer_issuer_id</c> (blinded-path-only
    /// offers), this method returns without error; full Merkle-root verification
    /// would be needed to authenticate such invoices.
    /// </remarks>
    /// <param name="bolt12Invoice">The fetched BOLT 12 invoice (<c>lni1…</c>).</param>
    /// <param name="bolt12Offer">The original BOLT 12 offer (<c>lno1…</c>) the invoice was requested for.</param>
    /// <exception cref="FormatException">
    /// Either string is not valid BOLT 12, or the invoice is missing <c>invoice_node_id</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <c>invoice_node_id</c> does not match <c>offer_issuer_id</c> — the invoice
    /// was not issued by the offer's owner.
    /// </exception>
    public static void VerifyInvoiceMatchesOffer(string bolt12Invoice, string bolt12Offer)
    {
        var offerIssuerId = ExtractOfferIssuerId(bolt12Offer);
        if (offerIssuerId is null) return; // blinded-path-only offer, cannot verify

        var invoiceNodeId = ExtractNodeId(bolt12Invoice);

        if (!invoiceNodeId.AsSpan().SequenceEqual(offerIssuerId))
            throw new InvalidOperationException(
                "BOLT 12 invoice_node_id does not match offer_issuer_id — " +
                "the invoice was not issued by the offer's owner.");
    }

    // ─── Bech32 without-checksum codec ────────────────────────────────────────

    /// <summary>
    /// Decodes a bech32-without-checksum string (BOLT 12 encoding) and returns
    /// the raw 8-bit byte payload. The HRP and the <c>1</c> separator are
    /// stripped; no checksum is expected or validated.
    /// </summary>
    internal static byte[] DecodeBolt12Bech32(string lower)
    {
        var sep = lower.LastIndexOf('1');
        if (sep < 0)
            throw new FormatException("BOLT 12 string has no '1' separator.");

        var dataPart = lower[(sep + 1)..];
        if (dataPart.Length == 0)
            throw new FormatException("BOLT 12 string has empty data part.");

        var values = new byte[dataPart.Length];
        for (var i = 0; i < dataPart.Length; i++)
        {
            var c = dataPart[i];
            var v = c < 128 ? Bech32CharMap[(int)c] : (byte)255;
            if (v == 255)
                throw new FormatException(
                    $"Invalid bech32 character '{c}' at position {sep + 1 + i}.");
            values[i] = v;
        }

        return ConvertBits(values, fromBits: 5, toBits: 8);
    }

    /// <summary>
    /// Encodes raw bytes as a bech32-without-checksum string.
    /// Used by tests to construct synthetic BOLT 12 invoice/offer strings.
    /// </summary>
    internal static string EncodeBolt12Bech32(string hrp, byte[] data)
    {
        var fiveBit = ConvertBitsEncode(data);
        var chars = new char[hrp.Length + 1 + fiveBit.Length];
        hrp.ToLowerInvariant().CopyTo(0, chars, 0, hrp.Length);
        chars[hrp.Length] = '1';
        for (var i = 0; i < fiveBit.Length; i++)
            chars[hrp.Length + 1 + i] = Bech32Alphabet[fiveBit[i]];
        return new string(chars);
    }

    private static byte[] ConvertBits(byte[] data, int fromBits, int toBits)
    {
        var acc = 0;
        var bits = 0;
        var result = new List<byte>(data.Length * fromBits / toBits + 1);
        var maxVal = (1 << toBits) - 1;

        foreach (var v in data)
        {
            acc = (acc << fromBits) | v;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxVal));
            }
        }

        // Remaining bits must be zero padding and less than fromBits.
        if (bits >= fromBits || ((acc << (toBits - bits)) & maxVal) != 0)
            throw new FormatException("BOLT 12 bech32 data has invalid padding.");

        return result.ToArray();
    }

    private static byte[] ConvertBitsEncode(byte[] data)
    {
        var acc = 0;
        var bits = 0;
        var result = new List<byte>(data.Length * 8 / 5 + 1);

        foreach (var v in data)
        {
            acc = (acc << 8) | v;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Add((byte)((acc >> bits) & 0x1f));
            }
        }
        if (bits > 0)
            result.Add((byte)((acc << (5 - bits)) & 0x1f));

        return result.ToArray();
    }

    // ─── TLV helpers ──────────────────────────────────────────────────────────

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
