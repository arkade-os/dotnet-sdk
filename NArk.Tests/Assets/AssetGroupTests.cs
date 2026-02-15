using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class AssetGroupTests
{
    private static readonly string ValidTxidHex = "0102030405060708091011121314151617181920212223242526272829303132";

    [Test]
    public void Transfer_WithAssetId_RoundTrips()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var inputs = new[] { AssetInput.Create(0, 100) };
        var outputs = new[] { AssetOutput.Create(0, 50), AssetOutput.Create(1, 50) };

        var group = AssetGroup.Create(assetId, null, inputs, outputs, []);
        var bytes = group.Serialize();
        var reader = new BufferReader(bytes);
        var restored = AssetGroup.FromReader(reader);

        Assert.That(restored.AssetId, Is.Not.Null);
        Assert.That(restored.AssetId!.GroupIndex, Is.EqualTo(0));
        Assert.That(restored.Inputs, Has.Count.EqualTo(1));
        Assert.That(restored.Outputs, Has.Count.EqualTo(2));
        Assert.That(restored.ControlAsset, Is.Null);
        Assert.That(restored.Metadata, Has.Count.EqualTo(0));
    }

    [Test]
    public void Issuance_NoInputs_WithControlAsset()
    {
        var controlRef = AssetRef.FromGroupIndex(0);
        var outputs = new[] { AssetOutput.Create(0, 1000) };

        var group = AssetGroup.Create(null, controlRef, [], outputs, []);
        Assert.That(group.IsIssuance, Is.True);

        var bytes = group.Serialize();
        var reader = new BufferReader(bytes);
        var restored = AssetGroup.FromReader(reader);

        Assert.That(restored.IsIssuance, Is.True);
        Assert.That(restored.ControlAsset, Is.Not.Null);
        Assert.That(restored.ControlAsset!.Type, Is.EqualTo(AssetRefType.ByGroup));
        Assert.That(restored.Inputs, Has.Count.EqualTo(0));
    }

    [Test]
    public void NonIssuance_WithControlAsset_Throws()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var controlRef = AssetRef.FromGroupIndex(0);

        Assert.Throws<ArgumentException>(() =>
            AssetGroup.Create(assetId, controlRef, [], [AssetOutput.Create(0, 100)], []));
    }

    [Test]
    public void Issuance_WithInputs_Throws()
    {
        var inputs = new[] { AssetInput.Create(0, 100) };
        var outputs = new[] { AssetOutput.Create(0, 100) };

        Assert.Throws<ArgumentException>(() =>
            AssetGroup.Create(null, null, inputs, outputs, []));
    }

    [Test]
    public void WithMetadata_SerializesInDescendingKeyOrder()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var meta = new[]
        {
            AssetMetadata.Create("alpha", "val1"),
            AssetMetadata.Create("zeta", "val2"),
            AssetMetadata.Create("beta", "val3"),
        };
        var outputs = new[] { AssetOutput.Create(0, 100) };
        var inputs = new[] { AssetInput.Create(0, 100) };

        var group = AssetGroup.Create(assetId, null, inputs, outputs, meta);
        var bytes = group.Serialize();
        var reader = new BufferReader(bytes);
        var restored = AssetGroup.FromReader(reader);

        Assert.That(restored.Metadata, Has.Count.EqualTo(3));
        // Deserialized order should be desc: zeta, beta, alpha
        Assert.That(restored.Metadata[0].KeyString, Is.EqualTo("zeta"));
        Assert.That(restored.Metadata[1].KeyString, Is.EqualTo("beta"));
        Assert.That(restored.Metadata[2].KeyString, Is.EqualTo("alpha"));
    }

    [Test]
    public void PresenceByte_CorrectFlags()
    {
        // Only AssetId → presence = 0x01
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var group = AssetGroup.Create(assetId, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        var bytes = group.Serialize();
        Assert.That(bytes[0], Is.EqualTo(0x01));
    }
}
