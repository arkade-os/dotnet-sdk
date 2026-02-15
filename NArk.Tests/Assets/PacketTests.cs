using NBitcoin;
using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class PacketTests
{
    private static readonly string ValidTxidHex = "0102030405060708091011121314151617181920212223242526272829303132";

    [Test]
    public void SimpleTransfer_SerializeAndParse_RoundTrips()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var inputs = new[] { AssetInput.Create(0, 100) };
        var outputs = new[] { AssetOutput.Create(0, 50), AssetOutput.Create(1, 50) };
        var group = AssetGroup.Create(assetId, null, inputs, outputs, []);

        var packet = Packet.Create([group]);
        var txOut = packet.ToTxOut();

        // Parse back
        var restored = Packet.FromScript(txOut.ScriptPubKey);
        Assert.That(restored.Groups, Has.Count.EqualTo(1));
        Assert.That(restored.Groups[0].Inputs, Has.Count.EqualTo(1));
        Assert.That(restored.Groups[0].Outputs, Has.Count.EqualTo(2));
        Assert.That(restored.Groups[0].Inputs[0].Amount, Is.EqualTo(100));
        Assert.That(restored.Groups[0].Outputs[0].Amount, Is.EqualTo(50));
    }

    [Test]
    public void IsAssetPacket_ValidScript_ReturnsTrue()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        var packet = Packet.Create([group]);
        var txOut = packet.ToTxOut();

        Assert.That(Packet.IsAssetPacket(txOut.ScriptPubKey), Is.True);
    }

    [Test]
    public void IsAssetPacket_NonAssetScript_ReturnsFalse()
    {
        var script = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(new byte[] { 0x01, 0x02, 0x03 }));
        Assert.That(Packet.IsAssetPacket(script), Is.False);
    }

    [Test]
    public void IsAssetPacket_EmptyScript_ReturnsFalse()
    {
        Assert.That(Packet.IsAssetPacket(Script.Empty), Is.False);
    }

    [Test]
    public void TxOut_HasZeroAmount()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        var packet = Packet.Create([group]);
        var txOut = packet.ToTxOut();

        Assert.That(txOut.Value, Is.EqualTo(Money.Zero));
    }

    [Test]
    public void MultipleGroups_RoundTrips()
    {
        var assetId1 = AssetId.Create(ValidTxidHex, 0);
        var assetId2 = AssetId.Create(ValidTxidHex, 1);

        var group1 = AssetGroup.Create(assetId1, null,
            [AssetInput.Create(0, 100)],
            [AssetOutput.Create(0, 100)], []);

        var group2 = AssetGroup.Create(assetId2, null,
            [AssetInput.Create(1, 200)],
            [AssetOutput.Create(1, 200)], []);

        var packet = Packet.Create([group1, group2]);
        var txOut = packet.ToTxOut();

        var restored = Packet.FromScript(txOut.ScriptPubKey);
        Assert.That(restored.Groups, Has.Count.EqualTo(2));
        Assert.That(restored.Groups[0].AssetId!.GroupIndex, Is.EqualTo(0));
        Assert.That(restored.Groups[1].AssetId!.GroupIndex, Is.EqualTo(1));
    }

    [Test]
    public void EmptyGroups_Throws()
    {
        Assert.Throws<ArgumentException>(() => Packet.Create([]));
    }

    [Test]
    public void InvalidControlAssetGroupIndex_Throws()
    {
        var controlRef = AssetRef.FromGroupIndex(5); // out of range
        var group = new AssetGroup(null, controlRef, [], [AssetOutput.Create(0, 1)], []);
        Assert.Throws<ArgumentException>(() => Packet.Create([group]));
    }

    [Test]
    public void Script_ContainsArkMagic()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        var packet = Packet.Create([group]);
        var scriptBytes = packet.Serialize();

        // The script should contain the ARK magic bytes somewhere in the data push
        var hex = Convert.ToHexString(scriptBytes).ToLowerInvariant();
        Assert.That(hex, Does.Contain("41524b00")); // ARK + marker
    }
}
