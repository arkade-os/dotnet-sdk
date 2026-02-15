namespace NArk.Core.Assets;

/// <summary>
/// An input in an asset group, referencing a transaction input (vin) and amount.
/// Binary layout:
///   Local (type=1): [0x01][2B vin LE][varint amount]
///   Intent (type=2): [0x02][32B txid][2B vin LE][varint amount]
/// </summary>
public class AssetInput
{
    public AssetInputType Type { get; }
    public ushort Vin { get; }
    public ulong Amount { get; }
    public byte[]? IntentTxid { get; }

    private AssetInput(AssetInputType type, ushort vin, ulong amount, byte[]? intentTxid)
    {
        Type = type;
        Vin = vin;
        Amount = amount;
        IntentTxid = intentTxid;
    }

    public static AssetInput Create(ushort vin, ulong amount)
    {
        var input = new AssetInput(AssetInputType.Local, vin, amount, null);
        input.Validate();
        return input;
    }

    public static AssetInput CreateIntent(string txidHex, ushort vin, ulong amount)
    {
        if (string.IsNullOrEmpty(txidHex))
            throw new ArgumentException("missing input intent txid");

        var txid = Convert.FromHexString(txidHex);
        if (txid.Length != AssetConstants.TxHashSize)
            throw new ArgumentException("invalid input intent txid length");

        var input = new AssetInput(AssetInputType.Intent, vin, amount, txid);
        input.Validate();
        return input;
    }

    public static AssetInput CreateIntent(byte[] txid, ushort vin, ulong amount)
    {
        if (txid.Length != AssetConstants.TxHashSize)
            throw new ArgumentException("invalid input intent txid length");

        var input = new AssetInput(AssetInputType.Intent, vin, amount, txid);
        input.Validate();
        return input;
    }

    public static AssetInput FromReader(BufferReader reader)
    {
        var type = (AssetInputType)reader.ReadByte();
        switch (type)
        {
            case AssetInputType.Local:
            {
                var vin = reader.ReadUint16LE();
                var amount = reader.ReadVarInt();
                return new AssetInput(AssetInputType.Local, vin, amount, null);
            }
            case AssetInputType.Intent:
            {
                if (reader.Remaining < AssetConstants.TxHashSize)
                    throw new ArgumentException("invalid input intent txid length");
                var txid = reader.ReadSlice(AssetConstants.TxHashSize);
                var vin = reader.ReadUint16LE();
                var amount = reader.ReadVarInt();
                var input = new AssetInput(AssetInputType.Intent, vin, amount, txid);
                input.Validate();
                return input;
            }
            case AssetInputType.Unspecified:
                throw new ArgumentException("asset input type unspecified");
            default:
                throw new ArgumentException($"asset input type {type} unknown");
        }
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
        if (Type == AssetInputType.Intent)
            writer.Write(IntentTxid!);
        writer.WriteUint16LE(Vin);
        writer.WriteVarInt(Amount);
    }

    public void Validate()
    {
        if (Type == AssetInputType.Intent && IntentTxid != null && IntentTxid.All(b => b == 0))
            throw new ArgumentException("missing input intent txid");
    }

    public override string ToString() => Convert.ToHexString(Serialize()).ToLowerInvariant();
}
