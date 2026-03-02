using NBitcoin;

namespace NArk.Core.Assets;

/// <summary>
/// An extension blob carried in a Bitcoin OP_RETURN output.
/// Wire format: OP_RETURN &lt;push: [ARK magic 0x41524B][type(1B) varint(len) data]...&gt;
/// Each record is a typed packet with explicit LEB128 varint length framing.
/// Duplicate packet types are rejected. At least one packet is required.
/// </summary>
public class Extension
{
    public static readonly byte[] ArkadeMagic = [0x41, 0x52, 0x4B]; // "ARK"

    public IReadOnlyList<IExtensionPacket> Packets { get; }

    public Extension(IReadOnlyList<IExtensionPacket> packets)
    {
        Packets = packets;
    }

    /// <summary>
    /// Returns the asset Packet embedded in this extension, or null if none is present.
    /// </summary>
    public Packet? GetAssetPacket()
    {
        foreach (var p in Packets)
        {
            if (p is Packet packet)
                return packet;
        }
        return null;
    }

    /// <summary>
    /// Reports whether the given script is an ARK extension blob
    /// (OP_RETURN followed by a data push whose payload begins with ArkadeMagic).
    /// </summary>
    public static bool IsExtension(Script script)
    {
        var ops = script.ToOps().ToList();
        if (ops.Count < 2 || ops[0].Code != OpcodeType.OP_RETURN)
            return false;

        var data = ops[1].PushData;
        if (data is null || data.Length < ArkadeMagic.Length)
            return false;

        return data.AsSpan(0, ArkadeMagic.Length).SequenceEqual(ArkadeMagic);
    }

    /// <summary>
    /// Parses an extension from an OP_RETURN script.
    /// </summary>
    public static Extension FromScript(Script script)
    {
        var ops = script.ToOps().ToList();
        if (ops.Count == 0)
            throw new ArgumentException("missing OP_RETURN");
        if (ops[0].Code != OpcodeType.OP_RETURN)
            throw new ArgumentException("expected OP_RETURN");

        // Concatenate all data pushes after OP_RETURN
        using var ms = new MemoryStream();
        for (var i = 1; i < ops.Count; i++)
        {
            if (ops[i].PushData is { } pushData)
                ms.Write(pushData, 0, pushData.Length);
        }
        var payload = ms.ToArray();

        return FromPayload(payload);
    }

    /// <summary>
    /// Parses an extension from raw payload bytes (after OP_RETURN push extraction).
    /// Payload = [ARK magic][type varint(len) data]...
    /// </summary>
    private static Extension FromPayload(byte[] payload)
    {
        var reader = new BufferReader(payload);

        // Read and verify magic prefix
        if (reader.Remaining < ArkadeMagic.Length)
            throw new ArgumentException("missing magic prefix");

        var magic = reader.ReadSlice(ArkadeMagic.Length);
        if (!magic.AsSpan().SequenceEqual(ArkadeMagic))
            throw new ArgumentException(
                $"expected magic prefix {Convert.ToHexString(ArkadeMagic).ToLowerInvariant()}, " +
                $"got {Convert.ToHexString(magic).ToLowerInvariant()}");

        // Read TLV records
        var packets = new List<IExtensionPacket>();
        try
        {
            while (reader.Remaining > 0)
            {
                var packetType = reader.ReadByte();
                var packetData = reader.ReadVarSlice();

                var packet = ParsePacket(packetType, packetData);
                packets.Add(packet);
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException("missing packet data", ex);
        }

        if (packets.Count == 0)
            throw new ArgumentException("missing packets");

        // Prevent duplicate packet types
        var seen = new HashSet<byte>();
        foreach (var p in packets)
        {
            if (!seen.Add(p.PacketType))
                throw new ArgumentException($"duplicate packet type {p.PacketType}");
        }

        return new Extension(packets);
    }

    /// <summary>
    /// Searches a transaction's outputs for an extension blob and parses it.
    /// Returns null if no extension output is found.
    /// </summary>
    public static Extension? FromTransaction(Transaction tx)
    {
        foreach (var output in tx.Outputs)
        {
            if (IsExtension(output.ScriptPubKey))
                return FromScript(output.ScriptPubKey);
        }
        return null;
    }

    /// <summary>
    /// Serializes this extension as a complete OP_RETURN script.
    /// </summary>
    public byte[] Serialize()
    {
        var writer = new BufferWriter();
        writer.Write(ArkadeMagic);

        foreach (var packet in Packets)
        {
            var packetBytes = packet.SerializePacketData();
            writer.WriteByte(packet.PacketType);
            writer.WriteVarSlice(packetBytes);
        }

        return BuildOpReturnScript(writer.ToBytes());
    }

    /// <summary>
    /// Returns an OP_RETURN TxOut with amount=0 containing the serialized extension.
    /// </summary>
    public TxOut ToTxOut()
    {
        var scriptBytes = Serialize();
        return new TxOut(Money.Zero, new Script(scriptBytes));
    }

    private static IExtensionPacket ParsePacket(byte packetType, byte[] data)
    {
        return packetType switch
        {
            Packet.PacketTypeId => Packet.FromBytes(data),
            _ => new UnknownPacket(packetType, data)
        };
    }

    /// <summary>
    /// Builds an OP_RETURN script with the correct push opcode for the data length.
    /// Bypasses NBitcoin's ScriptBuilder which caps elements at 520 bytes.
    /// </summary>
    internal static byte[] BuildOpReturnScript(byte[] data)
    {
        var n = data.Length;
        using var ms = new MemoryStream(2 + n);
        ms.WriteByte((byte)OpcodeType.OP_RETURN);

        switch (n)
        {
            case <= 75:
                ms.WriteByte((byte)n);
                break;
            case <= 255:
                ms.WriteByte((byte)OpcodeType.OP_PUSHDATA1);
                ms.WriteByte((byte)n);
                break;
            case <= 65535:
            {
                ms.WriteByte((byte)OpcodeType.OP_PUSHDATA2);
                ms.WriteByte((byte)(n & 0xFF));
                ms.WriteByte((byte)((n >> 8) & 0xFF));
                break;
            }
            default:
            {
                ms.WriteByte((byte)OpcodeType.OP_PUSHDATA4);
                ms.WriteByte((byte)(n & 0xFF));
                ms.WriteByte((byte)((n >> 8) & 0xFF));
                ms.WriteByte((byte)((n >> 16) & 0xFF));
                ms.WriteByte((byte)((n >> 24) & 0xFF));
                break;
            }
        }

        ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}
