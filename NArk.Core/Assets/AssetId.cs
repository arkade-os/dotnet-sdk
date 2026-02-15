using System.Globalization;

namespace NArk.Core.Assets;

/// <summary>
/// 34-byte asset identifier: 32-byte genesis txid + uint16 group index.
/// Binary layout: [32B txid][2B groupIndex LE]
/// </summary>
public class AssetId
{
    public byte[] Txid { get; }
    public ushort GroupIndex { get; }

    private AssetId(byte[] txid, ushort groupIndex)
    {
        Txid = txid;
        GroupIndex = groupIndex;
    }

    public static AssetId Create(string txidHex, ushort groupIndex)
    {
        if (string.IsNullOrEmpty(txidHex))
            throw new ArgumentException("missing txid");

        var txid = Convert.FromHexString(txidHex);
        if (txid.Length != AssetConstants.TxHashSize)
            throw new ArgumentException($"invalid txid length: got {txid.Length} bytes, want {AssetConstants.TxHashSize} bytes");

        var assetId = new AssetId(txid, groupIndex);
        assetId.Validate();
        return assetId;
    }

    public static AssetId FromString(string hex)
    {
        var buf = Convert.FromHexString(hex);
        return FromBytes(buf);
    }

    public static AssetId FromBytes(byte[] buf)
    {
        if (buf is not { Length: > 0 })
            throw new ArgumentException("missing asset id");
        if (buf.Length != AssetConstants.AssetIdSize)
            throw new ArgumentException($"invalid asset id length: got {buf.Length} bytes, want {AssetConstants.AssetIdSize} bytes");

        var reader = new BufferReader(buf);
        return FromReader(reader);
    }

    public static AssetId FromReader(BufferReader reader)
    {
        if (reader.Remaining < AssetConstants.AssetIdSize)
            throw new ArgumentException($"invalid asset id length: got {reader.Remaining}, want {AssetConstants.AssetIdSize}");

        var txid = reader.ReadSlice(AssetConstants.TxHashSize);
        var index = reader.ReadUint16LE();
        var assetId = new AssetId(txid, index);
        assetId.Validate();
        return assetId;
    }

    public byte[] Serialize()
    {
        var writer = new BufferWriter();
        SerializeTo(writer);
        return writer.ToBytes();
    }

    public void SerializeTo(BufferWriter writer)
    {
        writer.Write(Txid);
        writer.WriteUint16LE(GroupIndex);
    }

    public void Validate()
    {
        if (Txid.All(b => b == 0))
            throw new ArgumentException("empty txid");
    }

    public override string ToString() => Convert.ToHexString(Serialize()).ToLowerInvariant();
}
