namespace NArk.Core.Assets;

/// <summary>
/// A group of asset inputs and outputs with optional AssetId, control asset, and metadata.
/// Binary layout:
///   [1B presence flags]
///   [if 0x01: 34B AssetId]
///   [if 0x02: AssetRef (variable)]
///   [if 0x04: varint mdCount + Metadata[] sorted desc by key]
///   [varint inputCount][AssetInput...]
///   [varint outputCount][AssetOutput...]
/// </summary>
public class AssetGroup
{
    public AssetId? AssetId { get; }
    public AssetRef? ControlAsset { get; }
    public IReadOnlyList<AssetInput> Inputs { get; }
    public IReadOnlyList<AssetOutput> Outputs { get; }
    public IReadOnlyList<AssetMetadata> Metadata { get; }

    public AssetGroup(
        AssetId? assetId,
        AssetRef? controlAsset,
        IReadOnlyList<AssetInput> inputs,
        IReadOnlyList<AssetOutput> outputs,
        IReadOnlyList<AssetMetadata> metadata)
    {
        AssetId = assetId;
        ControlAsset = controlAsset;
        Inputs = inputs;
        Outputs = outputs;
        Metadata = metadata;
    }

    public static AssetGroup Create(
        AssetId? assetId,
        AssetRef? controlAsset,
        IReadOnlyList<AssetInput> inputs,
        IReadOnlyList<AssetOutput> outputs,
        IReadOnlyList<AssetMetadata> metadata)
    {
        var group = new AssetGroup(assetId, controlAsset, inputs, outputs, metadata);
        group.Validate();
        return group;
    }

    public bool IsIssuance => AssetId is null;

    public static AssetGroup FromReader(BufferReader reader)
    {
        var presence = reader.ReadByte();

        AssetId? assetId = null;
        AssetRef? controlAsset = null;
        IReadOnlyList<AssetMetadata> metadata = [];

        if ((presence & AssetConstants.MaskAssetId) != 0)
            assetId = Assets.AssetId.FromReader(reader);

        if ((presence & AssetConstants.MaskControlAsset) != 0)
            controlAsset = AssetRef.FromReader(reader);

        if ((presence & AssetConstants.MaskMetadata) != 0)
            metadata = DeserializeMetadataList(reader);

        var inputCount = (int)reader.ReadVarInt();
        var inputs = new List<AssetInput>(inputCount);
        for (var i = 0; i < inputCount; i++)
            inputs.Add(AssetInput.FromReader(reader));

        var outputCount = (int)reader.ReadVarInt();
        var outputs = new List<AssetOutput>(outputCount);
        for (var i = 0; i < outputCount; i++)
            outputs.Add(AssetOutput.FromReader(reader));

        var group = new AssetGroup(assetId, controlAsset, inputs, outputs, metadata);
        group.Validate();
        return group;
    }

    public byte[] Serialize()
    {
        Validate();
        var writer = new BufferWriter();
        SerializeTo(writer);
        return writer.ToBytes();
    }

    public void SerializeTo(BufferWriter writer)
    {
        byte presence = 0;
        if (AssetId is not null) presence |= AssetConstants.MaskAssetId;
        if (ControlAsset is not null) presence |= AssetConstants.MaskControlAsset;
        if (Metadata.Count > 0) presence |= AssetConstants.MaskMetadata;
        writer.WriteByte(presence);

        if (AssetId is not null) AssetId.SerializeTo(writer);
        if (ControlAsset is not null) ControlAsset.SerializeTo(writer);
        if (Metadata.Count > 0) SerializeMetadataList(Metadata, writer);

        writer.WriteVarInt((ulong)Inputs.Count);
        foreach (var input in Inputs)
            input.SerializeTo(writer);

        writer.WriteVarInt((ulong)Outputs.Count);
        foreach (var output in Outputs)
            output.SerializeTo(writer);
    }

    public void Validate()
    {
        if (IsIssuance)
        {
            if (Inputs.Count != 0)
                throw new ArgumentException("issuance must have no inputs");
        }
        else
        {
            if (ControlAsset is not null)
                throw new ArgumentException("only issuance can have a control asset");
        }
    }

    /// <summary>
    /// Metadata is sorted by key in descending order before serialization (matching TS/Go).
    /// </summary>
    private static void SerializeMetadataList(IReadOnlyList<AssetMetadata> metadata, BufferWriter writer)
    {
        writer.WriteVarInt((ulong)metadata.Count);
        var sorted = metadata.OrderByDescending(m => m.KeyString, StringComparer.Ordinal).ToList();
        foreach (var m in sorted)
            m.SerializeTo(writer);
    }

    private static IReadOnlyList<AssetMetadata> DeserializeMetadataList(BufferReader reader)
    {
        var count = (int)reader.ReadVarInt();
        var metadata = new List<AssetMetadata>(count);
        for (var i = 0; i < count; i++)
            metadata.Add(AssetMetadata.FromReader(reader));
        return metadata;
    }

    public override string ToString() => Convert.ToHexString(Serialize()).ToLowerInvariant();
}
