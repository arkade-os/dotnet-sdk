using NBitcoin;

namespace NArk.Core.Assets;

/// <summary>
/// A collection of AssetGroups serialized into an OP_RETURN output.
/// Wire format: OP_RETURN &lt;push: [ARK magic 0x41524B][0x00 marker][varint groupCount][AssetGroup...]&gt;
/// </summary>
public class Packet
{
    public IReadOnlyList<AssetGroup> Groups { get; }

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

    public static Packet FromScript(Script script)
    {
        var rawPacket = ExtractRawPacketFromScript(script);
        var reader = new BufferReader(rawPacket);
        return FromReader(reader);
    }

    public static bool IsAssetPacket(Script script)
    {
        try
        {
            ExtractRawPacketFromScript(script);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns an OP_RETURN TxOut with amount=0 containing the serialized packet.
    /// </summary>
    public TxOut ToTxOut()
    {
        var data = SerializePacketData();
        var payload = new byte[AssetConstants.ArkadeMagic.Length + 1 + data.Length];
        Array.Copy(AssetConstants.ArkadeMagic, 0, payload, 0, AssetConstants.ArkadeMagic.Length);
        payload[AssetConstants.ArkadeMagic.Length] = AssetConstants.MarkerAssetPayload;
        Array.Copy(data, 0, payload, AssetConstants.ArkadeMagic.Length + 1, data.Length);

        var script = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(payload));
        return new TxOut(Money.Zero, script);
    }

    /// <summary>
    /// Returns the full OP_RETURN script bytes.
    /// </summary>
    public byte[] Serialize()
    {
        return ToTxOut().ScriptPubKey.ToBytes();
    }

    public void Validate()
    {
        if (Groups.Count == 0)
            throw new ArgumentException("missing assets");

        foreach (var group in Groups)
        {
            if (group.ControlAsset is { Type: AssetRefType.ByGroup } controlRef
                && controlRef.GroupIndex >= Groups.Count)
            {
                throw new ArgumentException(
                    $"invalid control asset group index, {controlRef.GroupIndex} out of range [0, {Groups.Count - 1}]");
            }
        }
    }

    private byte[] SerializePacketData()
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

    private static byte[] ExtractRawPacketFromScript(Script script)
    {
        var ops = script.ToOps().ToList();
        if (ops.Count == 0 || ops[0].Code != OpcodeType.OP_RETURN)
            throw new ArgumentException("OP_RETURN not found in output script");

        // Concatenate all data pushes after OP_RETURN
        using var ms = new MemoryStream();
        for (var i = 1; i < ops.Count; i++)
        {
            if (ops[i].PushData is { } pushData)
                ms.Write(pushData, 0, pushData.Length);
        }
        var payload = ms.ToArray();

        if (payload.Length < AssetConstants.ArkadeMagic.Length + 1)
            throw new ArgumentException("invalid OP_RETURN data");

        // Verify magic prefix
        for (var i = 0; i < AssetConstants.ArkadeMagic.Length; i++)
        {
            if (payload[i] != AssetConstants.ArkadeMagic[i])
                throw new ArgumentException(
                    $"invalid magic prefix, got {Convert.ToHexString(payload[..AssetConstants.ArkadeMagic.Length]).ToLowerInvariant()} want {Convert.ToHexString(AssetConstants.ArkadeMagic).ToLowerInvariant()}");
        }

        // Verify marker
        var marker = payload[AssetConstants.ArkadeMagic.Length];
        if (marker != AssetConstants.MarkerAssetPayload)
            throw new ArgumentException($"invalid asset marker, got {marker} want {AssetConstants.MarkerAssetPayload}");

        var packetData = new byte[payload.Length - AssetConstants.ArkadeMagic.Length - 1];
        Array.Copy(payload, AssetConstants.ArkadeMagic.Length + 1, packetData, 0, packetData.Length);

        if (packetData.Length == 0)
            throw new ArgumentException("missing packet data");

        return packetData;
    }

    public override string ToString() => Convert.ToHexString(Serialize()).ToLowerInvariant();
}
