using NBitcoin;
using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class PacketTests
{
    // Fixture: "issuance of self-controlled asset"
    [Test]
    public void Issuance_SelfControlled_RawPacketMatchesExpected()
    {
        var controlRef = AssetRef.FromGroupIndex(0);
        var outputs = new[] { AssetOutput.Create(0, 21000000) };
        var group = AssetGroup.Create(null, controlRef, [], outputs, []);
        var packet = Packet.Create([group]);
        // Raw packet data (no OP_RETURN/Extension wrapper)
        Assert.That(ToHex(packet.SerializePacketData()),
            Is.EqualTo("01020200000001010000c0de810a"));
    }

    // Fixture: "issuance of many assets controlled by a single one"
    [Test]
    public void Issuance_ManyControlled_RawPacketMatchesExpected()
    {
        var group0 = AssetGroup.Create(null, AssetRef.FromGroupIndex(3), [],
            [AssetOutput.Create(1, 100)],
            [AssetMetadata.Create("ticker", "TEST")]);

        var group1 = AssetGroup.Create(null, AssetRef.FromGroupIndex(3), [],
            [AssetOutput.Create(1, 300)],
            [AssetMetadata.Create("ticker", "TEST2")]);

        var group2 = AssetGroup.Create(null, AssetRef.FromGroupIndex(3), [],
            [AssetOutput.Create(0, 2100)],
            [AssetMetadata.Create("ticker", "TEST3")]);

        var group3 = AssetGroup.Create(null, null, [],
            [AssetOutput.Create(2, 1)],
            [AssetMetadata.Create("ticker", "TEST3"), AssetMetadata.Create("desc", "control_asset")]);

        var packet = Packet.Create([group0, group1, group2, group3]);
        Assert.That(ToHex(packet.SerializePacketData()),
            Is.EqualTo("040602030001067469636b657204544553540001010100640602030001067469636b65720554455354320001010100ac020602030001067469636b65720554455354330001010000b4100402067469636b657205544553543304646573630d636f6e74726f6c5f6173736574000101020001"));
    }

    [Test]
    public void Serialize_ProducesExtensionWrappedScript()
    {
        var controlRef = AssetRef.FromGroupIndex(0);
        var outputs = new[] { AssetOutput.Create(0, 21000000) };
        var group = AssetGroup.Create(null, controlRef, [], outputs, []);
        var packet = Packet.Create([group]);
        var serialized = packet.Serialize();
        // Should be an OP_RETURN script with Extension wrapper
        var script = new Script(serialized);
        Assert.That(Extension.IsExtension(script), Is.True);
        // Roundtrip through Extension
        var ext = Extension.FromScript(script);
        var restored = ext.GetAssetPacket();
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.Groups, Has.Count.EqualTo(1));
    }

    [Test]
    public void FromString_ParsesRawPacketHex()
    {
        var packet = Packet.FromString("01020200000001010000c0de810a");
        Assert.That(packet.Groups, Has.Count.EqualTo(1));
        Assert.That(packet.Groups[0].IsIssuance, Is.True);
    }

    [Test]
    public void FromBytes_ParsesRawPacketBytes()
    {
        var data = Convert.FromHexString("01020200000001010000c0de810a");
        var packet = Packet.FromBytes(data);
        Assert.That(packet.Groups, Has.Count.EqualTo(1));
    }

    // Round-trip tests
    [Test]
    public void SimpleTransfer_RoundTrips()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var group = AssetGroup.Create(assetId, null,
            [AssetInput.Create(0, 100)],
            [AssetOutput.Create(0, 50), AssetOutput.Create(1, 50)], []);
        var packet = Packet.Create([group]);
        var restored = Packet.FromScript(packet.ToTxOut().ScriptPubKey);
        Assert.That(restored.Groups, Has.Count.EqualTo(1));
        Assert.That(restored.Groups[0].Inputs[0].Amount, Is.EqualTo(100));
        Assert.That(restored.Groups[0].Outputs[0].Amount, Is.EqualTo(50));
    }

    [Test]
    public void Extension_IsExtension_ValidScript_ReturnsTrue()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        Assert.That(Extension.IsExtension(Packet.Create([group]).ToTxOut().ScriptPubKey), Is.True);
    }

    [Test]
    public void Extension_IsExtension_NonAssetScript_ReturnsFalse()
    {
        var script = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(new byte[] { 0x01, 0x02, 0x03 }));
        Assert.That(Extension.IsExtension(script), Is.False);
    }

    [Test]
    public void Extension_IsExtension_EmptyScript_ReturnsFalse()
    {
        Assert.That(Extension.IsExtension(Script.Empty), Is.False);
    }

    [Test]
    public void TxOut_HasZeroAmount()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        Assert.That(Packet.Create([group]).ToTxOut().Value, Is.EqualTo(Money.Zero));
    }

    // Invalid tests
    [Test]
    public void EmptyGroups_Throws()
    {
        Assert.Throws<ArgumentException>(() => Packet.Create([]));
    }

    [Test]
    public void InvalidControlAssetGroupIndex_Throws()
    {
        var group = new AssetGroup(null, AssetRef.FromGroupIndex(1), [], [AssetOutput.Create(0, 1)], []);
        Assert.Throws<ArgumentException>(() => Packet.Create([group]));
    }

    [Test]
    public void ToString_ReturnsRawPacketHex()
    {
        var controlRef = AssetRef.FromGroupIndex(0);
        var outputs = new[] { AssetOutput.Create(0, 21000000) };
        var group = AssetGroup.Create(null, controlRef, [], outputs, []);
        var packet = Packet.Create([group]);
        // ToString should return raw packet hex (not full script)
        Assert.That(packet.ToString(), Is.EqualTo("01020200000001010000c0de810a"));
    }

    [Test]
    public void FromString_Empty_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => Packet.FromString(""));
        Assert.That(ex!.Message, Does.Contain("missing packet data"));
    }

    [Test]
    public void FromString_InvalidHex_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => Packet.FromString("invalid"));
        Assert.That(ex!.Message, Does.Contain("invalid packet format, must be hex"));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
