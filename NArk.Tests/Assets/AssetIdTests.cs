using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class AssetIdTests
{
    // Fixture: valid AssetId vectors from ts-sdk
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", (ushort)0,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa0000", TestName = "zero index")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", (ushort)65535,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaffff", TestName = "max index")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", (ushort)2,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa0200", TestName = "random txid and index")]
    public void Create_ValidFixtures_SerializesToExpected(string txid, ushort index, string expectedHex)
    {
        var assetId = AssetId.Create(txid, index);
        Assert.That(assetId.ToString(), Is.EqualTo(expectedHex));
    }

    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa0000",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", (ushort)0, TestName = "deserialize zero index")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa0200",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", (ushort)2, TestName = "deserialize index 2")]
    public void FromString_ValidFixtures_DeserializesCorrectly(string hex, string expectedTxid, ushort expectedIndex)
    {
        var assetId = AssetId.FromString(hex);
        Assert.That(Convert.ToHexString(assetId.Txid).ToLowerInvariant(), Is.EqualTo(expectedTxid));
        Assert.That(assetId.GroupIndex, Is.EqualTo(expectedIndex));
    }

    [Test]
    public void Create_EmptyTxid_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetId.Create("", 0));
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

    [Test]
    public void FromString_EmptyTxid_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetId.FromString("00000000000000000000000000000000000000000000000000000000000000000100"));
    }

    [Test]
    public void RoundTrip()
    {
        var original = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 42);
        var hex = original.ToString();
        var restored = AssetId.FromString(hex);
        Assert.That(restored.ToString(), Is.EqualTo(hex));
    }

    [Test]
    public void Create_ValidTxid_SerializesTo34Bytes()
    {
        var assetId = AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0);
        Assert.That(assetId.Serialize().Length, Is.EqualTo(34));
    }
}
