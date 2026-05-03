using System.Buffers.Binary;

namespace NArk.Arkade.Introspector;

/// <summary>
/// Codec for the Introspector Packet — the TLV payload (type tag <c>0x01</c>)
/// the Arkade Extension envelope carries to bind ArkadeScript to specific
/// transaction inputs.
/// </summary>
/// <remarks>
/// <para>
/// Wire format inside the TLV payload:
/// <code>
/// compactSize(entry_count) +
///   per entry: u16_le(vin) +
///              compactSize(script_len) + script_bytes +
///              compactSize(witness_len) + witness_bytes
/// </code>
/// where <c>compactSize</c> is Bitcoin's standard variable-length integer
/// encoding. <c>witness_bytes</c> is opaque from the packet's perspective —
/// see <see cref="EncodePushList"/> / <see cref="DecodePushList"/> for the
/// canonical "list of pushes" sub-encoding most consumers use.
/// </para>
/// <para>
/// 1:1 with the introspector reference at
/// <c>ArkLabsHQ/introspector pkg/arkade</c> and the ts-sdk's
/// <c>extension/introspector/packet.ts</c> — fixtures are vendored from the
/// introspector's <c>testdata/introspector_packet.json</c>.
/// </para>
/// </remarks>
public static class IntrospectorPacket
{
    /// <summary>The 1-byte TLV type tag the Extension envelope uses for an Introspector Packet.</summary>
    public const byte PacketType = 0x01;

    /// <summary>
    /// Validates and returns a defensive copy of the entries, applying the same
    /// rules as the introspector reference: non-empty entry list, non-empty
    /// scripts, unique <c>vin</c> values.
    /// </summary>
    public static IReadOnlyList<IntrospectorEntry> Validate(IReadOnlyList<IntrospectorEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new ArgumentException("empty introspector packet", nameof(entries));

        var seen = new HashSet<ushort>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Script);
            ArgumentNullException.ThrowIfNull(entry.Witness);
            if (entry.Script.Length == 0)
                throw new ArgumentException($"empty script for vin {entry.Vin}", nameof(entries));
            if (!seen.Add(entry.Vin))
                throw new ArgumentException($"duplicate vin {entry.Vin}", nameof(entries));
        }
        return entries.ToArray();
    }

    /// <summary>
    /// Serialise a list of <see cref="IntrospectorEntry"/> into the TLV-payload
    /// byte stream defined above. Entries are validated via
    /// <see cref="Validate"/> first.
    /// </summary>
    public static byte[] Serialize(IReadOnlyList<IntrospectorEntry> entries)
    {
        Validate(entries);
        using var ms = new MemoryStream();
        WriteCompactSize(ms, (ulong)entries.Count);
        foreach (var entry in entries)
        {
            WriteUInt16Le(ms, entry.Vin);
            WriteCompactSlice(ms, entry.Script);
            // Witness is a list of stack pushes — encode inline using the
            // standard `compactSize(num) + [compactSize(len)+bytes]*` shape
            // that matches Go's psbt.WriteTxWitness.
            WriteCompactSlice(ms, EncodePushList(entry.Witness));
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Parse a TLV-payload byte stream into <see cref="IntrospectorEntry"/> values.
    /// Throws <see cref="FormatException"/> on truncated input or trailing data,
    /// and <see cref="ArgumentException"/> on validation rules (empty packet,
    /// empty script, duplicate vin).
    /// </summary>
    public static IReadOnlyList<IntrospectorEntry> Parse(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var span = (ReadOnlySpan<byte>)payload;
        var pos = 0;

        var count = ReadCompactSize(span, ref pos);
        if (count > int.MaxValue)
            throw new FormatException($"introspector packet entry count too large: {count}");

        var entries = new List<IntrospectorEntry>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            var vin = ReadUInt16Le(span, ref pos);
            var script = ReadCompactSlice(span, ref pos);
            var witnessBytes = ReadCompactSlice(span, ref pos);
            var witness = DecodePushList(witnessBytes);
            entries.Add(new IntrospectorEntry(vin, script, witness));
        }

        if (pos != span.Length)
            throw new FormatException($"unexpected {span.Length - pos} trailing bytes");

        return Validate(entries);
    }

    /// <summary>
    /// Encode a list of stack pushes into the witness byte format the ts-sdk
    /// and introspector consume:
    /// <c>compactSize(num_pushes) + per push: compactSize(push_len) + push_bytes</c>.
    /// </summary>
    public static byte[] EncodePushList(IReadOnlyList<byte[]> pushes)
    {
        ArgumentNullException.ThrowIfNull(pushes);
        using var ms = new MemoryStream();
        WriteCompactSize(ms, (ulong)pushes.Count);
        foreach (var push in pushes)
        {
            ArgumentNullException.ThrowIfNull(push);
            WriteCompactSlice(ms, push);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Decode the inverse of <see cref="EncodePushList"/>. Throws
    /// <see cref="FormatException"/> if the byte stream isn't a well-formed
    /// list-of-pushes (truncated or with trailing bytes).
    /// </summary>
    public static IReadOnlyList<byte[]> DecodePushList(byte[] witness)
    {
        ArgumentNullException.ThrowIfNull(witness);
        var span = (ReadOnlySpan<byte>)witness;
        var pos = 0;

        var count = ReadCompactSize(span, ref pos);
        if (count > int.MaxValue)
            throw new FormatException($"witness push count too large: {count}");

        var pushes = new List<byte[]>((int)count);
        for (var i = 0UL; i < count; i++)
            pushes.Add(ReadCompactSlice(span, ref pos));

        if (pos != span.Length)
            throw new FormatException($"unexpected {span.Length - pos} trailing bytes in push list");

        return pushes;
    }

    // ─── compactSize / u16 LE / length-prefixed slice helpers ─────────────────

    private static void WriteCompactSize(Stream s, ulong value)
    {
        if (value < 0xfd)
        {
            s.WriteByte((byte)value);
        }
        else if (value <= 0xffff)
        {
            s.WriteByte(0xfd);
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
            s.Write(b);
        }
        else if (value <= 0xffffffff)
        {
            s.WriteByte(0xfe);
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value);
            s.Write(b);
        }
        else
        {
            s.WriteByte(0xff);
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, value);
            s.Write(b);
        }
    }

    private static ulong ReadCompactSize(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos + 1 > span.Length) throw new FormatException("compactSize: unexpected end of stream");
        var first = span[pos++];
        return first switch
        {
            < 0xfd => first,
            0xfd => ReadUInt16Le(span, ref pos),
            0xfe => ReadUInt32Le(span, ref pos),
            _ /* 0xff */ => ReadUInt64Le(span, ref pos),
        };
    }

    private static void WriteUInt16Le(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        s.Write(b);
    }

    private static ushort ReadUInt16Le(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos + 2 > span.Length) throw new FormatException("u16: unexpected end of stream");
        var value = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
        pos += 2;
        return value;
    }

    private static uint ReadUInt32Le(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos + 4 > span.Length) throw new FormatException("u32: unexpected end of stream");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
        pos += 4;
        return value;
    }

    private static ulong ReadUInt64Le(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos + 8 > span.Length) throw new FormatException("u64: unexpected end of stream");
        var value = BinaryPrimitives.ReadUInt64LittleEndian(span[pos..]);
        pos += 8;
        return value;
    }

    private static void WriteCompactSlice(Stream s, byte[] data)
    {
        WriteCompactSize(s, (ulong)data.Length);
        s.Write(data, 0, data.Length);
    }

    private static byte[] ReadCompactSlice(ReadOnlySpan<byte> span, ref int pos)
    {
        var len = ReadCompactSize(span, ref pos);
        if (len > int.MaxValue) throw new FormatException($"slice length too large: {len}");
        var size = (int)len;
        if (pos + size > span.Length) throw new FormatException("slice: unexpected end of stream");
        var bytes = span.Slice(pos, size).ToArray();
        pos += size;
        return bytes;
    }
}
