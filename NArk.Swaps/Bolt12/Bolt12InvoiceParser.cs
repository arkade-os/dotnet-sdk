using System.Security.Cryptography;

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
/// <b>Verification:</b> <see cref="VerifyInvoiceMatchesOffer"/> performs three checks
/// to confirm an invoice was generated from the specified offer:
/// (1) <c>invoice_node_id</c> must match <c>offer_issuer_id</c> or the final blinded
/// path hop; (2) the Merkle root of offer-range TLV fields (types 2–22) must be
/// identical in offer and invoice.
/// </remarks>
internal static class Bolt12InvoiceParser
{
    // BOLT 12 TLV field types (bolt12.md).
    private const ulong InvoicePaymentHashType = 168; // 0xA8 — invoice_payment_hash
    private const ulong InvoiceNodeIdType       = 176; // 0xB0 — invoice_node_id
    private const ulong OfferIssuerIdType       =  22; // 0x16 — offer_issuer_id
    private const ulong OfferPathsType          =  16; // 0x10 — offer_paths

    // Offer-range TLV types: types 2–22 are offer fields embedded in invoices.
    private const ulong OfferRangeMin = 2;
    private const ulong OfferRangeMax = 22;

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
        catch (Exception ex) when (ex is not FormatException)
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
    /// <paramref name="bolt12Offer"/> using three checks per BOLT 12 §Invoice:
    /// <list type="number">
    ///   <item>If the offer has an explicit <c>offer_issuer_id</c> (TLV 22),
    ///   <c>invoice_node_id</c> (TLV 176) must equal it.</item>
    ///   <item>Otherwise, if the offer has blinded paths (TLV 16),
    ///   <c>invoice_node_id</c> must equal the <c>blinded_node_id</c> of the
    ///   final hop in at least one of those paths.</item>
    ///   <item>The BOLT 12 Merkle root of the offer-range TLV fields (types 2–22)
    ///   in the invoice must equal the Merkle root computed from the same fields in
    ///   the original offer — confirming the invoice was generated from this specific
    ///   offer, not merely by the same key.</item>
    /// </list>
    /// </summary>
    /// <param name="bolt12Invoice">The fetched BOLT 12 invoice (<c>lni1…</c>).</param>
    /// <param name="bolt12Offer">The original BOLT 12 offer (<c>lno1…</c>) the invoice was requested for.</param>
    /// <exception cref="FormatException">
    /// Either string is not valid BOLT 12, or the invoice is missing <c>invoice_node_id</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The invoice's signing key does not match the offer, or the offer-range Merkle root
    /// is inconsistent — the invoice was not generated from this offer.
    /// </exception>
    public static void VerifyInvoiceMatchesOffer(string bolt12Invoice, string bolt12Offer)
    {
        var offerTlv = DecodeBolt12Bech32(bolt12Offer.ToLowerInvariant());
        var invoiceTlv = DecodeBolt12Bech32(bolt12Invoice.ToLowerInvariant());
        var anyCheckPerformed = false;

        var offerIssuerId = FindTlvRecord(offerTlv, OfferIssuerIdType);
        if (offerIssuerId is not null)
        {
            anyCheckPerformed = true;
            // Check 1: explicit issuer_id pubkey match.
            var invoiceNodeId = FindTlvRecord(invoiceTlv, InvoiceNodeIdType);
            if (invoiceNodeId is null || invoiceNodeId.Length != 33)
                throw new FormatException(
                    $"invoice_node_id (TLV type {InvoiceNodeIdType}) not found or has wrong length in BOLT 12 invoice.");
            if (!invoiceNodeId.AsSpan().SequenceEqual(offerIssuerId))
                throw new InvalidOperationException(
                    "BOLT 12 invoice_node_id does not match offer_issuer_id — " +
                    "the invoice was not issued by the offer's owner.");
        }
        else
        {
            // Check 2: blinded-path offer — invoice_node_id must equal the
            // blinded_node_id of the final hop in at least one of the offer's paths.
            var pathsValue = FindTlvRecord(offerTlv, OfferPathsType);
            if (pathsValue is not null && pathsValue.Length > 0)
            {
                var lastHops = ParseBlindedPathLastHops(pathsValue);
                if (lastHops.Count > 0)
                {
                    anyCheckPerformed = true;
                    var invoiceNodeIdBlinded = FindTlvRecord(invoiceTlv, InvoiceNodeIdType);
                    if (invoiceNodeIdBlinded is null)
                        throw new FormatException(
                            $"invoice_node_id (TLV type {InvoiceNodeIdType}) not found in BOLT 12 invoice.");
                    if (invoiceNodeIdBlinded.Length != 33)
                        throw new FormatException(
                            $"invoice_node_id (TLV type {InvoiceNodeIdType}) has wrong length " +
                            $"{invoiceNodeIdBlinded.Length} (expected 33) in BOLT 12 invoice.");
                    if (!lastHops.Any(hop => invoiceNodeIdBlinded.AsSpan().SequenceEqual(hop)))
                        throw new InvalidOperationException(
                            "BOLT 12 invoice_node_id does not match the final hop of any " +
                            "blinded path in the offer — the invoice was not issued by the offer's owner.");
                }
            }
        }

        // Check 3: the Merkle root of offer-range TLV fields (types 2–22) must be
        // identical in both the offer and the invoice, confirming the invoice was
        // generated from this specific offer.
        var offerRoot = ComputeOfferIdMerkleRoot(offerTlv);
        if (offerRoot is not null)
        {
            anyCheckPerformed = true;
            var invoiceRoot = ComputeOfferIdMerkleRoot(invoiceTlv);
            if (invoiceRoot is null || !offerRoot.AsSpan().SequenceEqual(invoiceRoot))
                throw new InvalidOperationException(
                    "BOLT 12 invoice offer_id (Merkle root of offer-range TLVs 2–22) does not match " +
                    "the original offer — the invoice was not generated from this offer.");
        }

        if (!anyCheckPerformed)
            throw new InvalidOperationException(
                "BOLT 12 offer has no verifiable fields (no issuer_id, no blinded paths, " +
                "no offer-range TLVs) — cannot confirm invoice authenticity.");
    }

    /// <summary>
    /// Parses the raw value of an <c>offer_paths</c> TLV record (type 16) and
    /// returns the <c>blinded_node_id</c> of the final hop in each blinded path.
    /// </summary>
    /// <remarks>
    /// Blinded path wire format (BOLT 4 §Route Blinding):
    /// <code>
    /// introduction_node  33 bytes (02/03 prefix) | 9 bytes sciddir
    /// blinding_point     33 bytes
    /// num_hops           u8
    /// per hop:
    ///   blinded_node_id  33 bytes
    ///   enc_data_len     u16 big-endian
    ///   enc_data         enc_data_len bytes
    /// </code>
    /// Multiple paths are concatenated in the TLV value.
    /// </remarks>
    internal static IReadOnlyList<byte[]> ParseBlindedPathLastHops(byte[] pathsValue)
    {
        var result = new List<byte[]>();
        var pos = 0;
        while (pos < pathsValue.Length)
        {
            var before = pos;
            if (!TryReadBlindedPathLastHop(pathsValue, ref pos, out var lastHop) || lastHop is null)
                break;
            if (pos <= before) break; // guard against infinite loop on malformed data
            result.Add(lastHop);
        }
        return result;
    }

    private static bool TryReadBlindedPathLastHop(byte[] data, ref int pos, out byte[]? lastHop)
    {
        lastHop = null;
        var start = pos;

        // introduction_node: 33 bytes when compressed pubkey (0x02/0x03 prefix),
        // 9 bytes when sciddir (short_channel_id || direction).
        if (pos >= data.Length) return false;
        var introLen = data[pos] is 0x02 or 0x03 ? 33 : 9;
        if (pos + introLen > data.Length) { pos = start; return false; }
        pos += introLen;

        // blinding_point: 33 bytes
        if (pos + 33 > data.Length) { pos = start; return false; }
        pos += 33;

        // num_hops: u8
        if (pos >= data.Length) { pos = start; return false; }
        var numHops = data[pos++];

        for (var hop = 0; hop < numHops; hop++)
        {
            if (pos + 33 > data.Length) { pos = start; return false; }
            var nodeId = data[pos..(pos + 33)];
            pos += 33;

            // encrypted_recipient_data: u16 big-endian length prefix
            if (pos + 2 > data.Length) { pos = start; return false; }
            var encLen = (data[pos] << 8) | data[pos + 1];
            pos += 2;

            if (pos + encLen > data.Length) { pos = start; return false; }
            pos += encLen;

            lastHop = nodeId; // keep updating — last assignment is the final hop
        }

        return lastHop is not null;
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
    /// Enumerates all TLV records in <paramref name="tlv"/>, yielding the type
    /// and the full raw wire bytes (type BigSize + length BigSize + value) for
    /// each record.
    /// </summary>
    internal static IEnumerable<(ulong Type, byte[] Raw)> EnumerateTlvRecordsRaw(byte[] tlv)
    {
        var pos = 0;
        while (pos < tlv.Length)
        {
            var start = pos;
            var type = ReadBigSize(tlv, ref pos);
            if (pos >= tlv.Length) yield break;
            var length = (int)ReadBigSize(tlv, ref pos);
            if (pos + length > tlv.Length) yield break;
            var raw = tlv[start..(pos + length)];
            pos += length;
            yield return (type, raw);
        }
    }

    /// <summary>
    /// Reads one Lightning BigSize-encoded <c>ulong</c> from <paramref name="data"/>
    /// at <paramref name="pos"/> and advances <paramref name="pos"/> past it.
    /// </summary>
    internal static ulong ReadBigSize(byte[] data, ref int pos)
    {
        if (pos >= data.Length)
            throw new FormatException("Truncated BigSize: no bytes remaining.");
        var b = data[pos++];
        return b switch
        {
            <= 0xFC => b,
            0xFD => pos + 2 <= data.Length
                ? (ulong)data[pos++] << 8 | data[pos++]
                : throw new FormatException("Truncated BigSize (expected 2 more bytes)."),
            0xFE => pos + 4 <= data.Length
                ? (ulong)data[pos++] << 24 | (ulong)data[pos++] << 16
                  | (ulong)data[pos++] << 8 | data[pos++]
                : throw new FormatException("Truncated BigSize (expected 4 more bytes)."),
            _ => pos + 8 <= data.Length
                ? (ulong)data[pos++] << 56 | (ulong)data[pos++] << 48
                  | (ulong)data[pos++] << 40 | (ulong)data[pos++] << 32
                  | (ulong)data[pos++] << 24 | (ulong)data[pos++] << 16
                  | (ulong)data[pos++] << 8 | data[pos++]
                : throw new FormatException("Truncated BigSize (expected 8 more bytes).")
        };
    }

    // ─── BOLT 12 Merkle hashing ───────────────────────────────────────────────

    /// <summary>
    /// Computes the BOLT 12 tagged hash:
    /// <c>H(tag, msg) = SHA256(SHA256(tag) || SHA256(tag) || msg)</c>.
    /// </summary>
    private static byte[] TaggedHash(ReadOnlySpan<byte> tag, ReadOnlySpan<byte> msg)
    {
        var tagHash = SHA256.HashData(tag);
        var input = new byte[64 + msg.Length];
        tagHash.CopyTo(input.AsSpan(0));
        tagHash.CopyTo(input.AsSpan(32));
        msg.CopyTo(input.AsSpan(64));
        return SHA256.HashData(input);
    }

    /// <summary>
    /// Encodes <paramref name="val"/> as a Lightning BigSize byte sequence.
    /// Used to produce the type bytes fed into the BOLT 12 Merkle nonce hash.
    /// </summary>
    private static byte[] BigSizeEncode(ulong val)
    {
        if (val <= 0xFC) return [(byte)val];
        if (val <= 0xFFFF) return [0xFD, (byte)(val >> 8), (byte)val];
        if (val <= 0xFFFF_FFFF)
            return [0xFE, (byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val];
        return
        [
            0xFF,
            (byte)(val >> 56), (byte)(val >> 48), (byte)(val >> 40), (byte)(val >> 32),
            (byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val,
        ];
    }

    /// <summary>
    /// Computes <c>H("LnBranch", min(a,b) || max(a,b))</c> — the branch node
    /// hash used in BOLT 12 Merkle tree construction.
    /// </summary>
    private static byte[] BranchHash(byte[] a, byte[] b)
    {
        var cmp = a.AsSpan().SequenceCompareTo(b.AsSpan());
        var (lo, hi) = cmp <= 0 ? (a, b) : (b, a);
        var input = new byte[64];
        lo.CopyTo(input.AsSpan(0));
        hi.CopyTo(input.AsSpan(32));
        return TaggedHash("LnBranch"u8, input);
    }

    /// <summary>
    /// Computes the BOLT 12 Merkle root over an ordered list of TLV records
    /// using the tagged-hash tree algorithm from BOLT 12 Appendix A.
    /// </summary>
    /// <remarks>
    /// For each record <c>r</c>:
    /// <list type="bullet">
    ///   <item><c>leaf = H("LnLeaf", r_raw)</c></item>
    ///   <item><c>nonce = H("LnNonce" || first_raw, BigSize(r.Type))</c></item>
    ///   <item><c>pair = H("LnBranch", sort(leaf, nonce))</c></item>
    /// </list>
    /// The pair nodes are then combined bottom-up: adjacent pairs are merged with
    /// <c>H("LnBranch", sort(…))</c>; an odd last node is promoted unchanged.
    /// </remarks>
    internal static byte[] ComputeMerkleRoot(IReadOnlyList<(ulong Type, byte[] Raw)> records)
    {
        if (records.Count == 0)
            throw new ArgumentException("Record list must not be empty.", nameof(records));

        var firstRaw = records[0].Raw;
        var nonceTagPrefix = new byte[7 + firstRaw.Length];
        "LnNonce"u8.CopyTo(nonceTagPrefix.AsSpan(0));
        firstRaw.CopyTo(nonceTagPrefix.AsSpan(7));

        // Compute per-record pair nodes.
        var nodes = new List<byte[]>(records.Count);
        foreach (var (type, raw) in records)
        {
            var leaf = TaggedHash("LnLeaf"u8, raw);
            var nonce = TaggedHash(nonceTagPrefix, BigSizeEncode(type));
            nodes.Add(BranchHash(leaf, nonce));
        }

        // Build tree: combine adjacent pairs; promote last unpaired node.
        while (nodes.Count > 1)
        {
            var next = new List<byte[]>((nodes.Count + 1) / 2);
            for (var i = 0; i + 1 < nodes.Count; i += 2)
                next.Add(BranchHash(nodes[i], nodes[i + 1]));
            if (nodes.Count % 2 == 1) next.Add(nodes[^1]);
            nodes = next;
        }

        return nodes[0];
    }

    /// <summary>
    /// Computes the BOLT 12 offer_id: the Merkle root of TLV records with types
    /// in the offer range (2–22) from <paramref name="tlvStream"/>.
    /// Returns <c>null</c> when no offer-range records are present.
    /// </summary>
    internal static byte[]? ComputeOfferIdMerkleRoot(byte[] tlvStream)
    {
        var offerRecords = EnumerateTlvRecordsRaw(tlvStream)
            .Where(r => r.Type >= OfferRangeMin && r.Type <= OfferRangeMax)
            .ToList();
        return offerRecords.Count == 0 ? null : ComputeMerkleRoot(offerRecords);
    }
}
