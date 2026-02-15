namespace NArk.Core.Assets;

/// <summary>
/// An output in an asset group, referencing a transaction output (vout) and amount.
/// Binary layout: [2B vout LE][varint amount]
/// </summary>
public class AssetOutput
{
    public ushort Vout { get; }
    public ulong Amount { get; }

    private AssetOutput(ushort vout, ulong amount)
    {
        Vout = vout;
        Amount = amount;
    }

    public static AssetOutput Create(ushort vout, ulong amount)
    {
        var output = new AssetOutput(vout, amount);
        output.Validate();
        return output;
    }

    public static AssetOutput FromReader(BufferReader reader)
    {
        if (reader.Remaining < 2)
            throw new ArgumentException("invalid asset output vout length");
        var vout = reader.ReadUint16LE();
        var amount = reader.ReadVarInt();
        var output = new AssetOutput(vout, amount);
        output.Validate();
        return output;
    }

    public byte[] Serialize()
    {
        var writer = new BufferWriter();
        SerializeTo(writer);
        return writer.ToBytes();
    }

    public void SerializeTo(BufferWriter writer)
    {
        writer.WriteUint16LE(Vout);
        writer.WriteVarInt(Amount);
    }

    public void Validate()
    {
        if (Amount == 0)
            throw new ArgumentException("asset output amount must be greater than 0");
    }

    public override string ToString() => Convert.ToHexString(Serialize()).ToLowerInvariant();
}
