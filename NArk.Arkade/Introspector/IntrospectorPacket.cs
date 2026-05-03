using System.Buffers.Binary;
using NArk.Core.Assets;
using NBitcoin;

namespace NArk.Arkade.Introspector;

/// <summary>
/// TLV record (type tag <c>0x01</c>) carried inside the same
/// <see cref="Extension"/> OP_RETURN envelope the asset packet uses, binding
/// ArkadeScript bytecode + a witness stack to specific transaction inputs
/// for introspector co-signing.
/// </summary>
/// <remarks>
/// <para>
/// Wire format inside the TLV payload:
/// <code>
/// compactSize(entry_count) +
///   per entry: u16_le(vin) +
///              compactSize(script_len) + script_bytes +
///              compactSize(witness_payload_len) + witness_payload
/// </code>
/// where <c>witness_payload</c> is the standard
/// <c>compactSize(num_pushes) + [compactSize(len)+bytes]*</c> shape produced
/// by Bitcoin's <c>WriteTxWitness</c> (matching the Go reference's
/// <c>wire.TxWitness</c>).
/// </para>
/// <para>
/// 1:1 with <c>ArkLabsHQ/introspector pkg/arkade/introspector_packet.go</c>
/// and the ts-sdk's <c>extension/introspector/packet.ts</c>. Fixtures are
/// vendored verbatim from the introspector's
/// <c>testdata/introspector_packet.json</c>.
/// </para>
/// </remarks>
public sealed class IntrospectorPacket : IExtensionPacket
{
    /// <summary>The 1-byte TLV type tag the Extension envelope uses for an Introspector Packet.</summary>
    public const byte PacketTypeId = 0x01;

    /// <summary>Validated, immutable list of entries.</summary>
    public IReadOnlyList<IntrospectorEntry> Entries { get; }

    /// <summary>
    /// Construct from a list of entries — validates non-empty packet,
    /// non-empty scripts, and unique <c>Vin</c> values.
    /// </summary>
    public IntrospectorPacket(IReadOnlyList<IntrospectorEntry> entries)
    {
        Entries = Validate(entries);
    }

    byte IExtensionPacket.PacketType => PacketTypeId;
    /// <inheritdoc cref="IExtensionPacket.SerializePacketData"/>
    public byte[] SerializePacketData() => SerializeEntries(Entries);

    /// <summary>
    /// Parse the inner TLV payload bytes into an <see cref="IntrospectorPacket"/>.
    /// Throws <see cref="FormatException"/> on truncated input or trailing
    /// data; <see cref="ArgumentException"/> on validation rule violations.
    /// </summary>
    public static IntrospectorPacket FromBytes(byte[] payload) => new(ParseEntries(payload));

    /// <summary>
    /// Look up the introspector packet riding alongside other packets in an
    /// already-parsed <see cref="Extension"/>. Returns <c>null</c> when
    /// the extension carries no introspector record. Re-parses the
    /// underlying <see cref="UnknownPacket"/> bytes — <see cref="Extension"/>
    /// itself doesn't know the introspector type natively (different package).
    /// </summary>
    public static IntrospectorPacket? FromExtension(Extension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        foreach (var p in extension.Packets)
        {
            switch (p)
            {
                case IntrospectorPacket already: return already;
                case UnknownPacket u when u.PacketType == PacketTypeId:
                    return FromBytes(u.Data);
            }
        }
        return null;
    }

    /// <summary>
    /// Convenience: search a transaction's outputs for an Extension OP_RETURN
    /// and return its embedded introspector packet, or <c>null</c>.
    /// </summary>
    public static IntrospectorPacket? FromTransaction(Transaction tx)
    {
        var ext = Extension.FromTransaction(tx);
        return ext is null ? null : FromExtension(ext);
    }

    // ─── Validation rules (matches the introspector reference) ────────

    /// <summary>
    /// Apply the introspector's validation rules: at least one entry,
    /// non-empty script per entry, unique <c>Vin</c>s. Returns a defensive
    /// copy on success; throws <see cref="ArgumentException"/> on violation.
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

    // ─── Witness "list of pushes" sub-encoding (public for callers
    //     that want to roundtrip the witness blob outside the packet) ──

    /// <summary>
    /// Encode a list of stack pushes in the standard
    /// <c>compactSize(num) + [compactSize(len)+bytes]*</c> shape — matches
    /// Bitcoin's <c>WriteTxWitness</c>.
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

    /// <summary>Inverse of <see cref="EncodePushList"/>.</summary>
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

    // ─── Entry list (TLV payload, no envelope) codec ──────────────────

    private static byte[] SerializeEntries(IReadOnlyList<IntrospectorEntry> entries)
    {
        using var ms = new MemoryStream();
        WriteCompactSize(ms, (ulong)entries.Count);
        foreach (var entry in entries)
        {
            WriteUInt16Le(ms, entry.Vin);
            WriteCompactSlice(ms, entry.Script);
            WriteCompactSlice(ms, EncodePushList(entry.Witness));
        }
        return ms.ToArray();
    }

    private static IReadOnlyList<IntrospectorEntry> ParseEntries(byte[] payload)
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

    // ─── compactSize / u16 LE / length-prefixed slice helpers ─────────

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
