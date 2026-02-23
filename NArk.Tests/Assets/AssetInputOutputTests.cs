using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class AssetInputTests
{
    // Fixture: valid single input vectors
    [Test]
    public void Local_SerializesToExpected()
    {
        var input = AssetInput.Create(5, 10);
        Assert.That(ToHex(input.Serialize()), Is.EqualTo("0105000a"));
    }

    [Test]
    public void Intent_SerializesToExpected()
    {
        var input = AssetInput.CreateIntent(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 77, 500);
        Assert.That(ToHex(input.Serialize()),
            Is.EqualTo("02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4d00f403"));
    }

    [Test]
    public void Local_RoundTrips()
    {
        var original = AssetInput.Create(5, 10);
        var restored = AssetInput.FromReader(new BufferReader(original.Serialize()));
        Assert.That(restored.Type, Is.EqualTo(AssetInputType.Local));
        Assert.That(restored.Vin, Is.EqualTo(5));
        Assert.That(restored.Amount, Is.EqualTo(10));
    }

    [Test]
    public void Intent_RoundTrips()
    {
        var original = AssetInput.CreateIntent(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 77, 500);
        var restored = AssetInput.FromReader(new BufferReader(original.Serialize()));
        Assert.That(restored.Type, Is.EqualTo(AssetInputType.Intent));
        Assert.That(restored.Vin, Is.EqualTo(77));
        Assert.That(restored.Amount, Is.EqualTo(500));
    }

    // Fixture: valid input list vectors (count-prefixed)
    [Test]
    public void SingleLocalInputList()
    {
        VerifyInputList(
            [AssetInput.Create(5, 10)],
            "010105000a");
    }

    [Test]
    public void ManyLocalInputList()
    {
        VerifyInputList(
            [
                AssetInput.Create(1, 10),
                AssetInput.Create(0, 2100000000),
                AssetInput.Create(25, 8400000000000)
            ],
            "030101000a01000080eaade90701190080c090b8bcf401");
    }

    [Test]
    public void SingleIntentInputList()
    {
        VerifyInputList(
            [AssetInput.CreateIntent("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 77, 500)],
            "0102aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4d00f403");
    }

    [Test]
    public void ManyIntentInputList()
    {
        VerifyInputList(
            [
                AssetInput.CreateIntent("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 77, 500),
                AssetInput.CreateIntent("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 1, 100000000000)
            ],
            "0202aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4d00f40302bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb010080d0dbc3f402");
    }

    // Fixture: invalid
    [Test]
    public void Intent_EmptyTxid_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetInput.CreateIntent("", 10, 500));
    }

    [Test]
    public void Intent_ZeroTxid_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetInput.CreateIntent("0000000000000000000000000000000000000000000000000000000000000000", 10, 500));
    }

    [Test]
    public void FromReader_UnspecifiedType_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetInput.FromReader(new BufferReader(Convert.FromHexString("0005000a"))));
    }

    [Test]
    public void FromReader_UnknownType_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetInput.FromReader(new BufferReader(Convert.FromHexString("0305000a"))));
    }

    // Fixture: invalid input lists (validated at AssetGroup level)
    [Test]
    public void MixedInputTypes_Throws()
    {
        var inputs = new[]
        {
            AssetInput.Create(1, 10),
            AssetInput.CreateIntent("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 77, 500)
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            AssetGroup.Create(
                AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0),
                null, inputs, [AssetOutput.Create(0, 10)], []));
        Assert.That(ex!.Message, Does.Contain("same type").IgnoreCase);
    }

    [Test]
    public void DuplicateVins_Throws()
    {
        var inputs = new[] { AssetInput.Create(1, 10), AssetInput.Create(1, 200) };
        var ex = Assert.Throws<ArgumentException>(() =>
            AssetGroup.Create(
                AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0),
                null, inputs, [AssetOutput.Create(0, 10)], []));
        Assert.That(ex!.Message, Does.Contain("duplicated inputs vin"));
    }

    private static void VerifyInputList(AssetInput[] inputs, string expectedHex)
    {
        var writer = new BufferWriter();
        writer.WriteVarInt((ulong)inputs.Length);
        foreach (var input in inputs)
            input.SerializeTo(writer);
        Assert.That(ToHex(writer.ToBytes()), Is.EqualTo(expectedHex));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

[TestFixture]
public class AssetOutputTests
{
    // Fixture: valid single output
    [Test]
    public void Create_SerializesToExpected()
    {
        var output = AssetOutput.Create(5, 10);
        Assert.That(ToHex(output.Serialize()), Is.EqualTo("0105000a"));
    }

    [Test]
    public void Create_RoundTrips()
    {
        var original = AssetOutput.Create(5, 10);
        var restored = AssetOutput.FromReader(new BufferReader(original.Serialize()));
        Assert.That(restored.Vout, Is.EqualTo(5));
        Assert.That(restored.Amount, Is.EqualTo(10));
    }

    [Test]
    public void Serialization_IncludesTypeByte()
    {
        // Wire format: [0x01 type][2B vout LE][varint amount]
        var output = AssetOutput.Create(5, 10);
        var bytes = output.Serialize();
        Assert.That(bytes[0], Is.EqualTo(0x01)); // type
        Assert.That(bytes[1], Is.EqualTo(0x05)); // vout low
        Assert.That(bytes[2], Is.EqualTo(0x00)); // vout high
        Assert.That(bytes[3], Is.EqualTo(0x0a)); // amount = 10
    }

    // Fixture: valid output list vectors
    [Test]
    public void SingleOutputList()
    {
        VerifyOutputList(
            [AssetOutput.Create(5, 10)],
            "010105000a");
    }

    [Test]
    public void ManyOutputList()
    {
        VerifyOutputList(
            [
                AssetOutput.Create(1, 10),
                AssetOutput.Create(0, 2100000000),
                AssetOutput.Create(25, 8400000000000)
            ],
            "030101000a01000080eaade90701190080c090b8bcf401");
    }

    // Fixture: invalid
    [Test]
    public void Create_ZeroAmount_Throws()
    {
        Assert.Throws<ArgumentException>(() => AssetOutput.Create(0, 0));
    }

    [Test]
    public void FromReader_UnspecifiedType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AssetOutput.FromReader(new BufferReader(Convert.FromHexString("00050001"))));
        Assert.That(ex!.Message, Does.Contain("unspecified"));
    }

    [Test]
    public void FromReader_UnknownType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AssetOutput.FromReader(new BufferReader(Convert.FromHexString("03050001"))));
        Assert.That(ex!.Message, Does.Contain("unknown"));
    }

    [Test]
    public void FromReader_ZeroAmount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AssetOutput.FromReader(new BufferReader(Convert.FromHexString("01050000"))));
    }

    [Test]
    public void DuplicateVouts_Throws()
    {
        var outputs = new[] { AssetOutput.Create(5, 10), AssetOutput.Create(5, 70) };
        var ex = Assert.Throws<ArgumentException>(() =>
            AssetGroup.Create(
                AssetId.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0),
                null, [AssetInput.Create(0, 80)], outputs, []));
        Assert.That(ex!.Message, Does.Contain("duplicated output vout"));
    }

    private static void VerifyOutputList(AssetOutput[] outputs, string expectedHex)
    {
        var writer = new BufferWriter();
        writer.WriteVarInt((ulong)outputs.Length);
        foreach (var output in outputs)
            output.SerializeTo(writer);
        Assert.That(ToHex(writer.ToBytes()), Is.EqualTo(expectedHex));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
