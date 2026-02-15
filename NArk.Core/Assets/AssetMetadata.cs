using System.Text;

namespace NArk.Core.Assets;

/// <summary>
/// Key-value metadata entry for an asset group.
/// Binary layout: [varslice key][varslice value]
/// </summary>
public class AssetMetadata
{
    public byte[] Key { get; }
    public byte[] Value { get; }

    private AssetMetadata(byte[] key, byte[] value)
    {
        Key = key;
        Value = value;
    }

    public static AssetMetadata Create(byte[] key, byte[] value)
    {
        var md = new AssetMetadata(key, value);
        md.Validate();
        return md;
    }

    public static AssetMetadata Create(string key, string value) =>
        Create(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));

    public static AssetMetadata FromReader(BufferReader reader)
    {
        byte[] key;
        byte[] value;
        try { key = reader.ReadVarSlice(); }
        catch { throw new ArgumentException("invalid metadata length"); }
        try { value = reader.ReadVarSlice(); }
        catch { throw new ArgumentException("invalid metadata length"); }
        var md = new AssetMetadata(key, value);
        md.Validate();
        return md;
    }

    public byte[] Serialize()
    {
        var writer = new BufferWriter();
        SerializeTo(writer);
        return writer.ToBytes();
    }

    public void SerializeTo(BufferWriter writer)
    {
        writer.WriteVarSlice(Key);
        writer.WriteVarSlice(Value);
    }

    public void Validate()
    {
        if (Key.Length == 0) throw new ArgumentException("missing metadata key");
        if (Value.Length == 0) throw new ArgumentException("missing metadata value");
    }

    public string KeyString => Encoding.UTF8.GetString(Key);
    public string ValueString => Encoding.UTF8.GetString(Value);

    public override string ToString() => Convert.ToHexString(Serialize()).ToLowerInvariant();
}
