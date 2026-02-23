using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class AssetGroupTests
{
    // Fixture: "send (with local inputs and outputs)"
    [Test]
    public void Send_SerializesToExpected()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", 0);
        var inputs = new[] { AssetInput.Create(0, 100) };
        var outputs = new[] { AssetOutput.Create(0, 60), AssetOutput.Create(1, 40) };
        var group = AssetGroup.Create(assetId, null, inputs, outputs, []);
        Assert.That(ToHex(group.Serialize()),
            Is.EqualTo("01aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa100000101000064020100003c01010028"));
    }

    // Fixture: "refresh (with intent inputs)"
    [Test]
    public void Refresh_WithIntentInputs_SerializesToExpected()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var inputs = new[] { AssetInput.CreateIntent(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 0, 100) };
        var outputs = new[] { AssetOutput.Create(0, 100) };
        var group = AssetGroup.Create(assetId, null, inputs, outputs, []);
        Assert.That(ToHex(group.Serialize()),
            Is.EqualTo("01aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa00000102bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb0000640101000064"));
    }

    // Fixture: "issuance (simple)"
    [Test]
    public void Issuance_Simple_SerializesToExpected()
    {
        var outputs = new[] { AssetOutput.Create(2, 100) };
        var group = AssetGroup.Create(null, null, [], outputs, []);
        Assert.That(ToHex(group.Serialize()), Is.EqualTo("00000101020064"));
    }

    // Fixture: "issuance (with metadata)"
    [Test]
    public void Issuance_WithMetadata_SerializesToExpected()
    {
        var outputs = new[] { AssetOutput.Create(2, 100) };
        var metadata = new[]
        {
            AssetMetadata.Create("key2", "\U0001f47e"),  // 👾
            AssetMetadata.Create("key1", "value1")
        };
        var group = AssetGroup.Create(null, null, [], outputs, metadata);
        Assert.That(ToHex(group.Serialize()),
            Is.EqualTo("0402046b65793204f09f91be046b6579310676616c756531000101020064"));
    }

    // Fixture: "issuance (with metadata and control asset ref by group index)"
    [Test]
    public void Issuance_WithControlAssetByGroup_SerializesToExpected()
    {
        var controlRef = AssetRef.FromGroupIndex(1);
        var outputs = new[] { AssetOutput.Create(1, 100), AssetOutput.Create(0, 1) };
        var metadata = new[]
        {
            AssetMetadata.Create("key2", "value2"),
            AssetMetadata.Create("key1", "value1")
        };
        var group = AssetGroup.Create(null, controlRef, [], outputs, metadata);
        Assert.That(ToHex(group.Serialize()),
            Is.EqualTo("0602010002046b6579320676616c756532046b6579310676616c75653100020101006401000001"));
    }

    // Fixture: "issuance (with metadata and control asset ref by id)"
    [Test]
    public void Issuance_WithControlAssetById_SerializesToExpected()
    {
        var controlAssetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 2);
        var controlRef = AssetRef.FromId(controlAssetId);
        var outputs = new[] { AssetOutput.Create(1, 100), AssetOutput.Create(0, 1) };
        var metadata = new[]
        {
            AssetMetadata.Create("key2", "value2"),
            AssetMetadata.Create("key1", "value1")
        };
        var group = AssetGroup.Create(null, controlRef, [], outputs, metadata);
        Assert.That(ToHex(group.Serialize()),
            Is.EqualTo("0601aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa020002046b6579320676616c756532046b6579310676616c75653100020101006401000001"));
    }

    // Fixture: "burn all (without outputs)"
    [Test]
    public void BurnAll_SerializesToExpected()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var inputs = new[] { AssetInput.Create(0, 100) };
        var group = AssetGroup.Create(assetId, null, inputs, [], []);
        Assert.That(ToHex(group.Serialize()),
            Is.EqualTo("01aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa0000010100006400"));
    }

    // Fixture: "burn some (with outputs)"
    [Test]
    public void BurnSome_SerializesToExpected()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var inputs = new[] { AssetInput.Create(0, 100) };
        var outputs = new[] { AssetOutput.Create(3, 80) };
        var group = AssetGroup.Create(assetId, null, inputs, outputs, []);
        Assert.That(ToHex(group.Serialize()),
            Is.EqualTo("01aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa000001010000640101030050"));
    }

    // Deserialization from fixture hex
    [Test]
    public void Send_DeserializesFromFixtureHex()
    {
        var hex = "01aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa100000101000064020100003c01010028";
        var group = AssetGroup.FromReader(new BufferReader(Convert.FromHexString(hex)));
        Assert.That(group.AssetId, Is.Not.Null);
        Assert.That(group.Inputs, Has.Count.EqualTo(1));
        Assert.That(group.Inputs[0].Type, Is.EqualTo(AssetInputType.Local));
        Assert.That(group.Inputs[0].Amount, Is.EqualTo(100));
        Assert.That(group.Outputs, Has.Count.EqualTo(2));
        Assert.That(group.Outputs[0].Amount, Is.EqualTo(60));
        Assert.That(group.Outputs[1].Amount, Is.EqualTo(40));
    }

    // Fixture: metadata preserves insertion order (no sorting)
    [Test]
    public void WithMetadata_PreservesInsertionOrder()
    {
        var meta = new[]
        {
            AssetMetadata.Create("alpha", "val1"),
            AssetMetadata.Create("zeta", "val2"),
            AssetMetadata.Create("beta", "val3"),
        };
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 100)], [AssetOutput.Create(0, 100)], meta);
        var restored = AssetGroup.FromReader(new BufferReader(group.Serialize()));
        Assert.That(restored.Metadata, Has.Count.EqualTo(3));
        Assert.That(restored.Metadata[0].KeyString, Is.EqualTo("alpha"));
        Assert.That(restored.Metadata[1].KeyString, Is.EqualTo("zeta"));
        Assert.That(restored.Metadata[2].KeyString, Is.EqualTo("beta"));
    }

    // Fixture: invalid
    [Test]
    public void Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetGroup.Create(null, null, [], [], []));
    }

    [Test]
    public void Issuance_WithInputs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetGroup.Create(null, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 2100)], []));
    }

    [Test]
    public void Transfer_WithControlAsset_Throws()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var controlRef = AssetRef.FromId(AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1));
        Assert.Throws<ArgumentException>(() =>
            AssetGroup.Create(assetId, controlRef, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 2100)], []));
    }

    [Test]
    public void PresenceByte_CorrectFlags()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        Assert.That(group.Serialize()[0], Is.EqualTo(0x01));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
