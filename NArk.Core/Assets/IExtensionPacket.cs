namespace NArk.Core.Assets;

/// <summary>
/// A typed data record that can be carried inside an Extension OP_RETURN output.
/// Each packet type is identified by a single byte and serialized as:
///   type(1B) + varint(len) + data
/// </summary>
public interface IExtensionPacket
{
    byte PacketType { get; }
    byte[] SerializePacketData();
}

/// <summary>
/// Opaque packet type for unknown/future TLV records.
/// Round-trips the raw data without interpreting it.
/// </summary>
public class UnknownPacket(byte packetType, byte[] data) : IExtensionPacket
{
    public byte PacketType { get; } = packetType;
    public byte[] Data { get; } = data;
    public byte[] SerializePacketData() => Data;
}
