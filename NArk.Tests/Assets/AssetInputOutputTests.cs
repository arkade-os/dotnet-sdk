using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class AssetInputTests
{
    [Test]
    public void Local_SerializesWithPrefix01()
    {
        var input = AssetInput.Create(0, 100);
        var bytes = input.Serialize();
        Assert.That(bytes[0], Is.EqualTo(0x01)); // Local type
    }

    [Test]
    public void Local_RoundTrips()
    {
        var original = AssetInput.Create(3, 500);
        var bytes = original.Serialize();
        var reader = new BufferReader(bytes);
        var restored = AssetInput.FromReader(reader);
        Assert.That(restored.Type, Is.EqualTo(AssetInputType.Local));
        Assert.That(restored.Vin, Is.EqualTo(3));
        Assert.That(restored.Amount, Is.EqualTo(500));
    }

    [Test]
    public void Intent_RoundTrips()
    {
        var txidHex = "0102030405060708091011121314151617181920212223242526272829303132";
        var original = AssetInput.CreateIntent(txidHex, 1, 1000);
        var bytes = original.Serialize();
        var reader = new BufferReader(bytes);
        var restored = AssetInput.FromReader(reader);
        Assert.That(restored.Type, Is.EqualTo(AssetInputType.Intent));
        Assert.That(restored.Vin, Is.EqualTo(1));
        Assert.That(restored.Amount, Is.EqualTo(1000));
        Assert.That(restored.IntentTxid, Is.Not.Null);
    }

    [Test]
    public void Intent_ZeroTxid_Throws()
    {
        var zeroTxid = "0000000000000000000000000000000000000000000000000000000000000000";
        Assert.Throws<ArgumentException>(() => AssetInput.CreateIntent(zeroTxid, 0, 100));
    }
}

[TestFixture]
public class AssetOutputTests
{
    [Test]
    public void Create_ValidOutput_RoundTrips()
    {
        var original = AssetOutput.Create(2, 750);
        var bytes = original.Serialize();
        var reader = new BufferReader(bytes);
        var restored = AssetOutput.FromReader(reader);
        Assert.That(restored.Vout, Is.EqualTo(2));
        Assert.That(restored.Amount, Is.EqualTo(750));
    }

    [Test]
    public void Create_ZeroAmount_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetOutput.Create(0, 0));
    }

    [Test]
    public void Serialization_Format()
    {
        // vout=1 (LE: 0x01, 0x00), amount=100 (varint: 0x64)
        var output = AssetOutput.Create(1, 100);
        var bytes = output.Serialize();
        Assert.That(bytes[0], Is.EqualTo(0x01)); // vout low
        Assert.That(bytes[1], Is.EqualTo(0x00)); // vout high
        Assert.That(bytes[2], Is.EqualTo(0x64)); // amount = 100
    }
}
