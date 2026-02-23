using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class AssetRefTests
{
    // Fixture: newAssetRefFromId
    [Test]
    public void FromId_SerializesToExpected()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 42);
        var assetRef = AssetRef.FromId(assetId);
        Assert.That(assetRef.ToString(),
            Is.EqualTo("01aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2a00"));
    }

    // Fixture: newAssetRefFromGroup
    [TestCase((ushort)0, "020000", TestName = "zero index")]
    [TestCase((ushort)5, "020500", TestName = "random index")]
    [TestCase((ushort)65535, "02ffff", TestName = "max index")]
    public void FromGroupIndex_SerializesToExpected(ushort index, string expectedHex)
    {
        var assetRef = AssetRef.FromGroupIndex(index);
        Assert.That(assetRef.ToString(), Is.EqualTo(expectedHex));
    }

    // Fixture: invalid
    [Test]
    public void FromBytes_EmptyRef_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetRef.FromBytes(Array.Empty<byte>()));
    }

    [Test]
    public void FromBytes_UnspecifiedType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AssetRef.FromBytes(Convert.FromHexString("000005")));
        Assert.That(ex!.Message, Does.Contain("unspecified"));
    }

    [Test]
    public void FromBytes_UnknownType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AssetRef.FromBytes(Convert.FromHexString("030005")));
        Assert.That(ex!.Message, Does.Contain("unknown"));
    }

    [Test]
    public void FromBytes_InvalidLength_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetRef.FromBytes(Convert.FromHexString("0200")));
    }

    [Test]
    public void ByID_RoundTrips()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7);
        var original = AssetRef.FromId(assetId);
        var restored = AssetRef.FromBytes(original.Serialize());
        Assert.That(restored.Type, Is.EqualTo(AssetRefType.ByID));
        Assert.That(restored.AssetId!.GroupIndex, Is.EqualTo(7));
    }

    [Test]
    public void ByGroup_RoundTrips()
    {
        var original = AssetRef.FromGroupIndex(42);
        var restored = AssetRef.FromBytes(original.Serialize());
        Assert.That(restored.Type, Is.EqualTo(AssetRefType.ByGroup));
        Assert.That(restored.GroupIndex, Is.EqualTo(42));
    }
}
