using NArk.Core.Assets;
using NArk.Core.Helpers;
using NBitcoin;

namespace NArk.Tests;

/// <summary>
/// What survives the input remap that runs when a PSBT reorders a spend's inputs.
/// </summary>
/// <remarks>
/// The remap exists because an asset group names its inputs by vin, and a PSBT is free to put them
/// somewhere else. What it must not do is treat the asset packet as the only thing in the OP_RETURN:
/// one extension carries every packet a spend needs, and the others are money-bearing too.
/// </remarks>
[TestFixture]
public class ExtensionInputRemappingTests
{
    private const string AssetIdHex =
        "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b20000";

    /// <summary>An Arkade offer's packet type — anything that is not the asset packet will do.</summary>
    private const byte OfferPacketType = 0x03;

    private static readonly byte[] OfferPayload = [0xde, 0xad, 0xbe, 0xef, 0x01, 0x02, 0x03, 0x04];

    private static Script ExtensionWith(params IExtensionPacket[] packets) =>
        new Extension(packets).ToTxOut().ScriptPubKey;

    private static Packet AssetPacketAtVin(ushort vin) =>
        Packet.Create([
            AssetGroup.Create(
                AssetId.FromString(AssetIdHex),
                controlAsset: null,
                inputs: [AssetInput.Create(vin, 1000)],
                outputs: [AssetOutput.Create(0, 1000)],
                metadata: [])
        ]);

    [Test]
    public void TheRemap_MovesTheAssetInputToItsNewVin()
    {
        var script = ExtensionWith(AssetPacketAtVin(0));

        var remapped = ArkTransactionBuilderRemapHost.Remap(script, new Dictionary<ushort, ushort> { [0] = 2 });

        var group = Extension.FromScript(remapped!.ScriptPubKey).GetAssetPacket()!.Groups.Single();
        Assert.That(group.Inputs.Single().Vin, Is.EqualTo(2));
    }

    [Test]
    public void TheRemap_KeepsEveryOtherPacket()
    {
        // The regression this file exists for. Rebuilding the output from the remapped asset packet
        // alone kept a valid, confirmable transaction and quietly dropped the Arkade offer beside it
        // — and a funding transaction with no offer in it is one every solver ignores, because there
        // is nothing to match a market against. No error is raised on either side: the deposit just
        // sits in the covenant until it is cancelled.
        var script = ExtensionWith(AssetPacketAtVin(0), new UnknownPacket(OfferPacketType, OfferPayload));

        var remapped = ArkTransactionBuilderRemapHost.Remap(script, new Dictionary<ushort, ushort> { [0] = 1 });

        var packets = Extension.FromScript(remapped!.ScriptPubKey).Packets;
        Assert.Multiple(() =>
        {
            Assert.That(packets.Select(p => p.PacketType), Does.Contain(OfferPacketType),
                "the offer packet must survive a remap it has nothing to do with");
            Assert.That(
                packets.OfType<UnknownPacket>().Single(p => p.PacketType == OfferPacketType).Data,
                Is.EqualTo(OfferPayload),
                "and survive it byte for byte");
            Assert.That(
                Extension.FromScript(remapped.ScriptPubKey).GetAssetPacket()!.Groups.Single().Inputs.Single().Vin,
                Is.EqualTo(1),
                "while the asset input still moves");
        });
    }

    [Test]
    public void AnExtensionCarryingNoAssetPacket_IsLeftAlone()
    {
        // Nothing to remap, and rewriting it anyway would be a chance to lose what it does carry.
        var script = ExtensionWith(new UnknownPacket(OfferPacketType, OfferPayload));

        var remapped = ArkTransactionBuilderRemapHost.Remap(script, new Dictionary<ushort, ushort> { [0] = 1 });

        Assert.That(remapped, Is.Null);
    }

    [Test]
    public void AVinTheMapDoesNotName_StaysWhereItIs()
    {
        var script = ExtensionWith(AssetPacketAtVin(3));

        var remapped = ArkTransactionBuilderRemapHost.Remap(script, new Dictionary<ushort, ushort> { [0] = 1 });

        var group = Extension.FromScript(remapped!.ScriptPubKey).GetAssetPacket()!.Groups.Single();
        Assert.That(group.Inputs.Single().Vin, Is.EqualTo(3));
    }
}

/// <summary>Reaches the remap, which is internal to the builder it belongs to.</summary>
internal static class ArkTransactionBuilderRemapHost
{
    public static TxOut? Remap(Script script, IReadOnlyDictionary<ushort, ushort> mapping) =>
        TransactionHelpers.ArkTransactionBuilder.RemapExtensionInputs(script, mapping);
}
