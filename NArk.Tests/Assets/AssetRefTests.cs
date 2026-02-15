using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class AssetRefTests
{
    private static readonly string ValidTxidHex = "0102030405060708091011121314151617181920212223242526272829303132";

    [Test]
    public void ByID_SerializesWithPrefix01()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var assetRef = AssetRef.FromId(assetId);
        var bytes = assetRef.Serialize();
        Assert.That(bytes[0], Is.EqualTo(0x01)); // ByID type
        Assert.That(bytes.Length, Is.EqualTo(35)); // 1 + 34
    }

    [Test]
    public void ByGroup_SerializesWithPrefix02()
    {
        var assetRef = AssetRef.FromGroupIndex(3);
        var bytes = assetRef.Serialize();
        Assert.That(bytes[0], Is.EqualTo(0x02)); // ByGroup type
        Assert.That(bytes.Length, Is.EqualTo(3)); // 1 + 2
        Assert.That(bytes[1], Is.EqualTo(0x03)); // index LE low byte
        Assert.That(bytes[2], Is.EqualTo(0x00)); // index LE high byte
    }

    [Test]
    public void ByID_RoundTrips()
    {
        var assetId = AssetId.Create(ValidTxidHex, 7);
        var original = AssetRef.FromId(assetId);
        var bytes = original.Serialize();
        var restored = AssetRef.FromBytes(bytes);
        Assert.That(restored.Type, Is.EqualTo(AssetRefType.ByID));
        Assert.That(restored.AssetId!.GroupIndex, Is.EqualTo(7));
    }

    [Test]
    public void ByGroup_RoundTrips()
    {
        var original = AssetRef.FromGroupIndex(42);
        var bytes = original.Serialize();
        var restored = AssetRef.FromBytes(bytes);
        Assert.That(restored.Type, Is.EqualTo(AssetRefType.ByGroup));
        Assert.That(restored.GroupIndex, Is.EqualTo(42));
    }
}
