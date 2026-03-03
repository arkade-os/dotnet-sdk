using NBitcoin;

namespace NArk.Core.Assets;

/// <summary>
/// A collection of AssetGroups representing the asset packet (type 0x00) inside an Extension.
/// Raw wire format: varint(groupCount) + AssetGroup...
/// </summary>
public class Packet : IExtensionPacket
{
    public const byte PacketTypeId = 0;

    public IReadOnlyList<AssetGroup> Groups { get; }

    byte IExtensionPacket.PacketType => PacketTypeId;

    private Packet(IReadOnlyList<AssetGroup> groups)
    {
        Groups = groups;
    }

    public static Packet Create(IReadOnlyList<AssetGroup> groups)
    {
        var packet = new Packet(groups);
        packet.Validate();
        return packet;
    }

    /// <summary>
    /// Parses a Packet from raw bytes (varint groupCount + groups).
    /// No OP_RETURN/magic/marker prefix expected.
    /// </summary>
    public static Packet FromBytes(byte[] data)
    {
        var reader = new BufferReader(data);
        return FromReader(reader);
    }

    /// <summary>
    /// Parses a Packet from a hex-encoded string of raw packet bytes.
    /// </summary>
    public static Packet FromString(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            throw new ArgumentException("missing packet data");
        byte[] data;
        try
        {
            data = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new ArgumentException("invalid packet format, must be hex");
        }
        return FromBytes(data);
    }

    /// <summary>
    /// Parses a Packet from an OP_RETURN script by delegating to Extension.
    /// </summary>
    public static Packet FromScript(Script script)
    {
        var ext = Extension.FromScript(script);
        return ext.GetAssetPacket()
            ?? throw new ArgumentException("no asset packet found in extension");
    }

    /// <summary>
    /// Returns the full OP_RETURN script bytes (wrapped in an Extension).
    /// </summary>
    public byte[] Serialize()
    {
        return new Extension([this]).Serialize();
    }

    /// <summary>
    /// Returns an OP_RETURN TxOut with amount=0 (wrapped in an Extension).
    /// </summary>
    public TxOut ToTxOut()
    {
        return new Extension([this]).ToTxOut();
    }

    public void Validate()
    {
        if (Groups.Count == 0)
            throw new ArgumentException("missing assets");

        var seenAssetIds = new HashSet<string>();
        foreach (var group in Groups)
        {
            if (group.AssetId is { } aid)
            {
                var key = aid.ToString();
                if (!seenAssetIds.Add(key))
                    throw new ArgumentException($"duplicate asset group for asset {key}");
            }

            if (group.ControlAsset is { Type: AssetRefType.ByGroup } controlRef
                && controlRef.GroupIndex >= Groups.Count)
            {
                throw new ArgumentException(
                    $"invalid control asset group index, {controlRef.GroupIndex} out of range [0, {Groups.Count - 1}]");
            }
        }
    }

    /// <summary>
    /// Serializes the raw packet data (varint groupCount + groups). No wrapper.
    /// </summary>
    public byte[] SerializePacketData()
    {
        var writer = new BufferWriter();
        writer.WriteVarInt((ulong)Groups.Count);
        foreach (var group in Groups)
            group.SerializeTo(writer);
        return writer.ToBytes();
    }

    private static Packet FromReader(BufferReader reader)
    {
        var count = (int)reader.ReadVarInt();
        var groups = new List<AssetGroup>(count);
        for (var i = 0; i < count; i++)
            groups.Add(AssetGroup.FromReader(reader));

        if (reader.Remaining > 0)
            throw new ArgumentException($"invalid packet length, left {reader.Remaining} unknown bytes to read");

        var packet = new Packet(groups);
        packet.Validate();
        return packet;
    }

    /// <summary>
    /// Converts this packet into a batch leaf packet by replacing all group inputs
    /// with a single Intent input referencing the given intent txid.
    /// </summary>
    public Packet LeafTxPacket(byte[] intentTxid)
    {
        var leafGroups = Groups.Select(g => g.ToBatchLeafAssetGroup(intentTxid)).ToList();
        return new Packet(leafGroups);
    }

    /// <summary>
    /// Returns the hex-encoded representation of the raw packet data.
    /// </summary>
    public override string ToString() => Convert.ToHexString(SerializePacketData()).ToLowerInvariant();
}
