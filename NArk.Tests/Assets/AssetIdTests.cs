using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class AssetIdTests
{
    private static readonly string ValidTxidHex = "0102030405060708091011121314151617181920212223242526272829303132";

    [Test]
    public void Create_ValidTxid_SerializesTo34Bytes()
    {
        var assetId = AssetId.Create(ValidTxidHex, 0);
        var bytes = assetId.Serialize();
        Assert.That(bytes.Length, Is.EqualTo(34));
    }

    [Test]
    public void Create_WithGroupIndex_SerializesCorrectly()
    {
        var assetId = AssetId.Create(ValidTxidHex, 5);
        var bytes = assetId.Serialize();
        // Last 2 bytes are LE group index: 5 = [0x05, 0x00]
        Assert.That(bytes[32], Is.EqualTo(0x05));
        Assert.That(bytes[33], Is.EqualTo(0x00));
    }

    [Test]
    public void FromBytes_RoundTrips()
    {
        var original = AssetId.Create(ValidTxidHex, 42);
        var bytes = original.Serialize();
        var restored = AssetId.FromBytes(bytes);
        Assert.That(restored.GroupIndex, Is.EqualTo(42));
        Assert.That(restored.Txid, Is.EqualTo(original.Txid));
    }

    [Test]
    public void ToString_RoundTrips()
    {
        var original = AssetId.Create(ValidTxidHex, 1);
        var hex = original.ToString();
        var restored = AssetId.FromString(hex);
        Assert.That(restored.ToString(), Is.EqualTo(hex));
    }

    [Test]
    public void Create_ZeroTxid_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetId.Create("0000000000000000000000000000000000000000000000000000000000000000", 0));
    }

    [Test]
    public void FromBytes_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetId.FromBytes(new byte[10]));
    }
}
