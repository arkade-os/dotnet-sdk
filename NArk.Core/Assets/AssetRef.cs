namespace NArk.Core.Assets;

/// <summary>
/// Reference to an asset, either by full AssetId or by group index within a Packet.
/// Binary layout:
///   ByID: [0x01][34B AssetId]
///   ByGroup: [0x02][2B groupIndex LE]
/// </summary>
public class AssetRef
{
    public AssetRefType Type { get; }
    public AssetId? AssetId { get; }
    public ushort? GroupIndex { get; }

    private AssetRef(AssetRefType type, AssetId? assetId, ushort? groupIndex)
    {
        Type = type;
        AssetId = assetId;
        GroupIndex = groupIndex;
    }

    public static AssetRef FromId(AssetId assetId) => new(AssetRefType.ByID, assetId, null);
    public static AssetRef FromGroupIndex(ushort groupIndex) => new(AssetRefType.ByGroup, null, groupIndex);

    public static AssetRef FromBytes(byte[] buf)
    {
        if (buf is not { Length: > 0 })
            throw new ArgumentException("missing asset ref");
        var reader = new BufferReader(buf);
        return FromReader(reader);
    }

    public static AssetRef FromReader(BufferReader reader)
    {
        var type = (AssetRefType)reader.ReadByte();
        return type switch
        {
            AssetRefType.ByID => new AssetRef(AssetRefType.ByID, Assets.AssetId.FromReader(reader), null),
            AssetRefType.ByGroup => reader.Remaining >= 2
                ? new AssetRef(AssetRefType.ByGroup, null, reader.ReadUint16LE())
                : throw new ArgumentException("invalid asset ref length"),
            AssetRefType.Unspecified => throw new ArgumentException("asset ref type unspecified"),
            _ => throw new ArgumentException($"asset ref type unknown {type}")
        };
    }

    public byte[] Serialize()
    {
        var writer = new BufferWriter();
        SerializeTo(writer);
        return writer.ToBytes();
    }

    public void SerializeTo(BufferWriter writer)
    {
        writer.WriteByte((byte)Type);
        switch (Type)
        {
            case AssetRefType.ByID:
                AssetId!.SerializeTo(writer);
                break;
            case AssetRefType.ByGroup:
                writer.WriteUint16LE(GroupIndex!.Value);
                break;
        }
    }

    public override string ToString() => Convert.ToHexString(Serialize()).ToLowerInvariant();
}
