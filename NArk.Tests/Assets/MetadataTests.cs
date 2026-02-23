using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class MetadataTests
{
    // Fixture: valid single metadata vectors
    [TestCase("testKey", "testValue", "07746573744b6579097465737456616c7565", TestName = "simple")]
    [TestCase("{\"key\": \"key\"}", "{\"value\": \"value\"}", "0e7b226b6579223a20226b6579227d127b2276616c7565223a202276616c7565227d", TestName = "nested json")]
    [TestCase("0", "1", "01300131", TestName = "short key value")]
    public void Create_ValidFixtures_SerializesToExpected(string key, string value, string expectedHex)
    {
        var md = AssetMetadata.Create(key, value);
        Assert.That(ToHex(md.Serialize()), Is.EqualTo(expectedHex));
    }

    [Test]
    public void Create_ChineseChars_SerializesToExpected()
    {
        // Fixture: "another alphabet" — key=钥匙, value=价值
        var md = AssetMetadata.Create("\u94a5\u5319", "\u4ef7\u503c");
        Assert.That(ToHex(md.Serialize()), Is.EqualTo("06e992a5e58c9906e4bbb7e580bc"));
    }

    [Test]
    public void Create_Emoji_SerializesToExpected()
    {
        // Fixture: "emoji" — key=🔑, value=👾
        var md = AssetMetadata.Create("\U0001f511", "\U0001f47e");
        Assert.That(ToHex(md.Serialize()), Is.EqualTo("04f09f949104f09f91be"));
    }

    [Test]
    public void Create_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetMetadata.Create("", "value"));
    }

    [Test]
    public void Create_EmptyValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetMetadata.Create("key", ""));
    }

    [Test]
    public void RoundTrip()
    {
        var original = AssetMetadata.Create("testKey", "testValue");
        var restored = AssetMetadata.FromReader(new BufferReader(original.Serialize()));
        Assert.That(restored.KeyString, Is.EqualTo("testKey"));
        Assert.That(restored.ValueString, Is.EqualTo("testValue"));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

[TestFixture]
public class MetadataListTests
{
    // Fixture: valid metadata list serialization — "asset data (with icon url)"
    [Test]
    public void AssetDataWithIconUrl_SerializesToExpected()
    {
        var list = new MetadataList([
            AssetMetadata.Create("name", "testAsset"),
            AssetMetadata.Create("ticker", "TST"),
            AssetMetadata.Create("decimals", "0"),
            AssetMetadata.Create("icon", "https://example.com/icon.png")
        ]);
        Assert.That(ToHex(list.Serialize()),
            Is.EqualTo("04046e616d6509746573744173736574067469636b65720354535408646563696d616c7301300469636f6e1c68747470733a2f2f6578616d706c652e636f6d2f69636f6e2e706e67"));
    }

    // Fixture: valid metadata list serialization — "asset data (with icon embedded)"
    [Test]
    public void AssetDataWithIconEmbedded_SerializesToExpected()
    {
        var list = new MetadataList([
            AssetMetadata.Create("name", "testAsset"),
            AssetMetadata.Create("ticker", "TST"),
            AssetMetadata.Create("decimals", "0"),
            AssetMetadata.Create("icon", "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA")
        ]);
        Assert.That(ToHex(list.Serialize()),
            Is.EqualTo("04046e616d6509746573744173736574067469636b65720354535408646563696d616c7301300469636f6e32646174613a696d6167652f706e673b6261736536342c6956424f5277304b47676f414141414e535568455567414141415541"));
    }

    // Fixture: valid metadata list serialization — "random data"
    [Test]
    public void RandomData_SerializesToExpected()
    {
        var list = new MetadataList([
            AssetMetadata.Create("testKey", "testValue"),
            AssetMetadata.Create("{\"key\": \"key\"}", "{\"value\": \"value\"}"),
            AssetMetadata.Create("\u94a5\u5319", "\u4ef7\u503c")
        ]);
        Assert.That(ToHex(list.Serialize()),
            Is.EqualTo("0307746573744b6579097465737456616c75650e7b226b6579223a20226b6579227d127b2276616c7565223a202276616c7565227d06e992a5e58c9906e4bbb7e580bc"));
    }

    // Fixture: metadata hash — "asset data (with icon url)"
    [Test]
    public void Hash_AssetDataWithIconUrl()
    {
        var list = new MetadataList([
            AssetMetadata.Create("name", "testAsset"),
            AssetMetadata.Create("ticker", "TST"),
            AssetMetadata.Create("decimals", "0"),
            AssetMetadata.Create("icon", "https://example.com/icon.png")
        ]);
        Assert.That(ToHex(list.Hash()),
            Is.EqualTo("3a95a202409e237d575c8425685daa3a5880cd16e3c069b0df5c6228a234ce7b"));
    }

    // Fixture: metadata hash — "asset data (with icon embedded)"
    [Test]
    public void Hash_AssetDataWithIconEmbedded()
    {
        var list = new MetadataList([
            AssetMetadata.Create("name", "testAsset"),
            AssetMetadata.Create("ticker", "TST"),
            AssetMetadata.Create("decimals", "0"),
            AssetMetadata.Create("icon", "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA")
        ]);
        Assert.That(ToHex(list.Hash()),
            Is.EqualTo("99d136d0240c44d5915bfcc45c63e6030f9d02910956932bcfe7bca63fb582f4"));
    }

    // Fixture: metadata hash — "random data"
    [Test]
    public void Hash_RandomData()
    {
        var list = new MetadataList([
            AssetMetadata.Create("testKey", "testValue"),
            AssetMetadata.Create("{\"key\": \"key\"}", "{\"value\": \"value\"}"),
            AssetMetadata.Create("\u94a5\u5319", "\u4ef7\u503c")
        ]);
        Assert.That(ToHex(list.Hash()),
            Is.EqualTo("405828ca96e62f281e1e861e08f92813ac445d5ca2092877078dba25c8654596"));
    }

    // Empty metadata hash throws
    [Test]
    public void Hash_Empty_Throws()
    {
        var list = new MetadataList([]);
        Assert.Throws<ArgumentException>(() => list.Hash());
    }

    // Round-trip from bytes
    [Test]
    public void FromBytes_RoundTrips()
    {
        var list = new MetadataList([
            AssetMetadata.Create("name", "testAsset"),
            AssetMetadata.Create("ticker", "TST")
        ]);
        var restored = MetadataList.FromBytes(list.Serialize());
        Assert.That(restored.Items, Has.Count.EqualTo(2));
        Assert.That(restored.Items[0].KeyString, Is.EqualTo("name"));
        Assert.That(restored.Items[1].KeyString, Is.EqualTo("ticker"));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
