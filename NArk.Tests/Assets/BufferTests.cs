using NArk.Core.Assets;

namespace NArk.Tests.Assets;

[TestFixture]
public class BufferTests
{
    [Test]
    public void WriteThenRead_VarInt_RoundTrips()
    {
        foreach (var value in new ulong[] { 0, 1, 127, 128, 255, 256, 16383, 16384, 0xFFFF, 0x10000, 0xFFFFFFFF, 0x100000000 })
        {
            var writer = new BufferWriter();
            writer.WriteVarInt(value);
            var reader = new BufferReader(writer.ToBytes());
            Assert.That(reader.ReadVarInt(), Is.EqualTo(value), $"Failed for value {value}");
            Assert.That(reader.Remaining, Is.EqualTo(0), $"Remaining bytes for value {value}");
        }
    }

    [Test]
    public void VarInt_Zero_SingleByte()
    {
        var writer = new BufferWriter();
        writer.WriteVarInt(0);
        var bytes = writer.ToBytes();
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x00 }));
    }

    [Test]
    public void VarInt_127_SingleByte()
    {
        var writer = new BufferWriter();
        writer.WriteVarInt(127);
        var bytes = writer.ToBytes();
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x7F }));
    }

    [Test]
    public void VarInt_128_TwoBytes()
    {
        // 128 = 0b10000000 → LEB128: [0x80, 0x01]
        var writer = new BufferWriter();
        writer.WriteVarInt(128);
        var bytes = writer.ToBytes();
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x80, 0x01 }));
    }

    [Test]
    public void VarInt_300_TwoBytes()
    {
        // 300 = 0b100101100 → LEB128: [0xAC, 0x02]
        var writer = new BufferWriter();
        writer.WriteVarInt(300);
        var bytes = writer.ToBytes();
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAC, 0x02 }));
    }

    [Test]
    public void WriteThenRead_VarSlice_RoundTrips()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var writer = new BufferWriter();
        writer.WriteVarSlice(data);
        var reader = new BufferReader(writer.ToBytes());
        var result = reader.ReadVarSlice();
        Assert.That(result, Is.EqualTo(data));
    }

    [Test]
    public void WriteThenRead_Uint16LE_RoundTrips()
    {
        var writer = new BufferWriter();
        writer.WriteUint16LE(0x0102);
        var bytes = writer.ToBytes();
        // Little-endian: low byte first
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x02, 0x01 }));

        var reader = new BufferReader(bytes);
        Assert.That(reader.ReadUint16LE(), Is.EqualTo(0x0102));
    }

    [Test]
    public void WriteThenRead_MultipleMixed_RoundTrips()
    {
        var writer = new BufferWriter();
        writer.WriteByte(0xAB);
        writer.WriteUint16LE(0x1234);
        writer.WriteVarInt(300);
        writer.WriteVarSlice(new byte[] { 0xDE, 0xAD });

        var reader = new BufferReader(writer.ToBytes());
        Assert.That(reader.ReadByte(), Is.EqualTo(0xAB));
        Assert.That(reader.ReadUint16LE(), Is.EqualTo(0x1234));
        Assert.That(reader.ReadVarInt(), Is.EqualTo(300));
        Assert.That(reader.ReadVarSlice(), Is.EqualTo(new byte[] { 0xDE, 0xAD }));
        Assert.That(reader.Remaining, Is.EqualTo(0));
    }

    [Test]
    public void Reader_ThrowsOnOverread()
    {
        var reader = new BufferReader([0x01]);
        reader.ReadByte();
        Assert.Throws<InvalidOperationException>(() => reader.ReadByte());
    }

    [Test]
    public void Reader_ReadSlice_ThrowsOnOverread()
    {
        var reader = new BufferReader([0x01, 0x02]);
        Assert.Throws<InvalidOperationException>(() => reader.ReadSlice(3));
    }
}
