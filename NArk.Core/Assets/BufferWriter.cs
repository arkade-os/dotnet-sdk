namespace NArk.Core.Assets;

/// <summary>
/// Binary serialization helper using unsigned LEB128 varint encoding,
/// matching the Go/TypeScript asset encoding implementations.
/// </summary>
public class BufferWriter
{
    private readonly List<byte> _buffer = [];

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            _buffer.Add(b);
    }

    public void WriteByte(byte value) => _buffer.Add(value);

    public void WriteUint16LE(ushort value)
    {
        _buffer.Add((byte)(value & 0xFF));
        _buffer.Add((byte)((value >> 8) & 0xFF));
    }

    /// <summary>
    /// Writes an unsigned LEB128 varint. Each byte stores 7 data bits;
    /// bit 7 is the continuation flag (1 = more bytes follow).
    /// </summary>
    public void WriteVarInt(ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
                b |= 0x80;
            _buffer.Add(b);
        } while (value > 0);
    }

    public void WriteVarSlice(ReadOnlySpan<byte> data)
    {
        WriteVarInt((ulong)data.Length);
        Write(data);
    }

    public byte[] ToBytes() => _buffer.ToArray();
}
