using NBitcoin;
using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class PacketTests
{
    // Fixture: "issuance of self-controlled asset"
    [Test]
    public void Issuance_SelfControlled_SerializesToExpected()
    {
        var controlRef = AssetRef.FromGroupIndex(0);
        var outputs = new[] { AssetOutput.Create(0, 21000000) };
        var group = AssetGroup.Create(null, controlRef, [], outputs, []);
        var packet = Packet.Create([group]);
        Assert.That(ToHex(packet.Serialize()),
            Is.EqualTo("6a1241524b0001020200000001010000c0de810a"));
    }

    // Fixture: "issuance of many assets controlled by a single one"
    [Test]
    public void Issuance_ManyControlled_SerializesToExpected()
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
        Assert.That(ToHex(packet.Serialize()),
            Is.EqualTo("6a4c7641524b00040602030001067469636b657204544553540001010100640602030001067469636b65720554455354320001010100ac020602030001067469636b65720554455354330001010000b4100402067469636b657205544553543304646573630d636f6e74726f6c5f6173736574000101020001"));
    }

    // Fixture: deserialization from script hex
    [Test]
    public void FromScript_ParsesFixtureScript()
    {
        var scriptHex = "6a4c7641524b00040602030001067469636b657204544553540001010100640602030001067469636b65720554455354320001010100ac020602030001067469636b65720554455354330001010000b4100402067469636b657205544553543304646573630d636f6e74726f6c5f6173736574000101020001";
        var script = new Script(Convert.FromHexString(scriptHex));
        var packet = Packet.FromScript(script);
        Assert.That(packet.Groups, Has.Count.EqualTo(4));
        Assert.That(packet.Groups[0].IsIssuance, Is.True);
        Assert.That(packet.Groups[0].ControlAsset!.GroupIndex, Is.EqualTo(3));
        Assert.That(packet.Groups[3].Metadata, Has.Count.EqualTo(2));
    }

    // Fixture: leafTxPacket "convert inputs to type intent"
    [Test]
    public void LeafTxPacket_ConvertsInputsToIntent()
    {
        var scriptHex = "6a4c7641524b00040602030001067469636b657204544553540001010100640602030001067469636b65720554455354320001010100ac020602030001067469636b65720554455354330001010000b4100402067469636b657205544553543304646573630d636f6e74726f6c5f6173736574000101020001";
        var script = new Script(Convert.FromHexString(scriptHex));
        var packet = Packet.FromScript(script);

        var intentTxid = Convert.FromHexString("09aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var leafPacket = packet.LeafTxPacket(intentTxid);

        var expectedHex = "6a4d060141524b00040602030001067469636b65720454455354010209aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa00000001010100640602030001067469636b6572055445535432010209aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa00000001010100ac020602030001067469636b6572055445535433010209aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa00000001010000b4100402067469636b657205544553543304646573630d636f6e74726f6c5f6173736574010209aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa0000000101020001";
        Assert.That(ToHex(leafPacket.Serialize()), Is.EqualTo(expectedHex));
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
    public void IsAssetPacket_ValidScript_ReturnsTrue()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        Assert.That(Packet.IsAssetPacket(Packet.Create([group]).ToTxOut().ScriptPubKey), Is.True);
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
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        Assert.That(Packet.Create([group]).ToTxOut().Value, Is.EqualTo(Money.Zero));
    }

    // Fixture: invalid
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
    public void Script_ContainsArkMagic()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        var hex = ToHex(Packet.Create([group]).Serialize());
        Assert.That(hex, Does.Contain("41524b00"));
    }

    /// <summary>
    /// Trailing TLV bytes after the known asset groups must be silently
    /// ignored so that older parsers stay forward-compatible with newer
    /// protocol versions that may append additional TLV fields.
    /// See: arkade-os/arkd#945
    /// </summary>
    [Test]
    public void FromScript_ToleratesTrailingTlvBytes()
    {
        // Start with a known-good self-controlled issuance packet.
        var controlRef = AssetRef.FromGroupIndex(0);
        var outputs = new[] { AssetOutput.Create(0, 21000000) };
        var group = AssetGroup.Create(null, controlRef, [], outputs, []);
        var goodPacket = Packet.Create([group]);

        // Serialize normally, then append arbitrary trailing bytes to the
        // OP_RETURN push data to simulate a newer TLV-encoded payload.
        var goodTxOut = goodPacket.ToTxOut();
        var ops = goodTxOut.ScriptPubKey.ToOps().ToList();
        var pushData = ops[1].PushData;
        var extended = new byte[pushData.Length + 6];
        Array.Copy(pushData, 0, extended, 0, pushData.Length);
        // Append fake trailing TLV: type=0xFF, length=3, value=0xDE,0xAD,0x01
        extended[pushData.Length]     = 0xFF;
        extended[pushData.Length + 1] = 0x03;
        extended[pushData.Length + 2] = 0xDE;
        extended[pushData.Length + 3] = 0xAD;
        extended[pushData.Length + 4] = 0x01;
        extended[pushData.Length + 5] = 0x42;

        var script = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(extended));

        // Must parse without throwing.
        var parsed = Packet.FromScript(script);
        Assert.That(parsed.Groups, Has.Count.EqualTo(1));
        Assert.That(parsed.Groups[0].IsIssuance, Is.True);
        Assert.That(parsed.Groups[0].Outputs[0].Amount, Is.EqualTo(21000000));
    }

    /// <summary>
    /// A preceding TLV record whose value byte is 0x00 creates a false
    /// "0x00 0x00" sequence. The scanner must prefer the non-empty candidate
    /// (real asset data) over the empty count=0 parse from the false marker.
    /// </summary>
    [Test]
    public void FromScript_FalseMarkerEmbeddedInPrecedingTlvRecord()
    {
        // Start with a known-good self-controlled issuance packet.
        var controlRef = AssetRef.FromGroupIndex(0);
        var outputs = new[] { AssetOutput.Create(0, 21000000) };
        var group = AssetGroup.Create(null, controlRef, [], outputs, []);
        var goodPacket = Packet.Create([group]);

        // Serialize and extract the raw OP_RETURN push data.
        var goodTxOut = goodPacket.ToTxOut();
        var ops = goodTxOut.ScriptPubKey.ToOps().ToList();
        var pushData = ops[1].PushData;

        // pushData = "ARK" + 0x00 + <asset groups>
        // Inject: "ARK" + 0x02 0x00 + 0x00 + <asset groups>
        // The 0x02 0x00 is a fake TLV record whose value byte is 0x00,
        // creating a false marker before the real 0x00 asset marker.
        var magic = pushData[..3]; // "ARK"
        var assetRecord = pushData[3..]; // 0x00 + groups

        var fakeTlv = new byte[] { 0x02, 0x00 }; // type=0x02, value=0x00
        var injected = new byte[magic.Length + fakeTlv.Length + assetRecord.Length];
        magic.CopyTo(injected, 0);
        fakeTlv.CopyTo(injected, magic.Length);
        assetRecord.CopyTo(injected, magic.Length + fakeTlv.Length);

        var injectedScript = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(injected));

        Assert.That(Packet.IsAssetPacket(injectedScript), Is.True);

        var parsed = Packet.FromScript(injectedScript);
        Assert.That(parsed.Groups, Has.Count.EqualTo(1));
        Assert.That(parsed.Groups[0].IsIssuance, Is.True);
        Assert.That(parsed.Groups[0].Outputs[0].Amount, Is.EqualTo(21000000));
    }

    /// <summary>
    /// The asset marker (0x00) may appear at any position in the TLV stream
    /// after the ARK magic, not necessarily first. For example an Introspector
    /// record (type 0x01) could precede the asset data.
    /// </summary>
    [Test]
    public void FromScript_ArbitraryTlvRecordOrder()
    {
        // Start with a known-good self-controlled issuance packet.
        var controlRef = AssetRef.FromGroupIndex(0);
        var outputs = new[] { AssetOutput.Create(0, 21000000) };
        var group = AssetGroup.Create(null, controlRef, [], outputs, []);
        var goodPacket = Packet.Create([group]);

        // Serialize and extract the raw OP_RETURN push data.
        var goodTxOut = goodPacket.ToTxOut();
        var ops = goodTxOut.ScriptPubKey.ToOps().ToList();
        var pushData = ops[1].PushData;

        // pushData = "ARK" + 0x00 + <asset groups>
        // Rearrange to: "ARK" + 0x01 0xCA 0xFE + 0x00 + <asset groups>
        var magic = pushData[..3]; // "ARK"
        var assetRecord = pushData[3..]; // 0x00 + groups

        var fakeIntrospector = new byte[] { 0x01, 0xCA, 0xFE };
        var reordered = new byte[magic.Length + fakeIntrospector.Length + assetRecord.Length];
        magic.CopyTo(reordered, 0);
        fakeIntrospector.CopyTo(reordered, magic.Length);
        assetRecord.CopyTo(reordered, magic.Length + fakeIntrospector.Length);

        var reorderedScript = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(reordered));

        // Must still recognize as valid asset packet.
        Assert.That(Packet.IsAssetPacket(reorderedScript), Is.True);

        // Must parse without throwing and produce the same group.
        var parsed = Packet.FromScript(reorderedScript);
        Assert.That(parsed.Groups, Has.Count.EqualTo(1));
        Assert.That(parsed.Groups[0].IsIssuance, Is.True);
        Assert.That(parsed.Groups[0].Outputs[0].Amount, Is.EqualTo(21000000));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
