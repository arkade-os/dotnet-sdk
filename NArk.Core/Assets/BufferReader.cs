namespace NArk.Core.Assets;

/// <summary>
/// Binary deserialization helper using unsigned LEB128 varint encoding,
/// matching the Go/TypeScript asset encoding implementations.
/// </summary>
public class BufferReader
{
    private readonly byte[] _data;
    private int _offset;

    public BufferReader(byte[] data)
    {
        _data = data;
    }

    public int Remaining => _data.Length - _offset;

    public byte ReadByte()
    {
        if (_offset >= _data.Length)
            throw new InvalidOperationException("unexpected end of buffer");
        return _data[_offset++];
    }

    public byte[] ReadSlice(int size)
    {
        if (_offset + size > _data.Length)
            throw new InvalidOperationException("unexpected end of buffer");
        var result = new byte[size];
        Array.Copy(_data, _offset, result, 0, size);
        _offset += size;
        return result;
    }

    public ushort ReadUint16LE()
    {
        if (_offset + 2 > _data.Length)
            throw new InvalidOperationException("unexpected end of buffer");
        var value = (ushort)(_data[_offset] | (_data[_offset + 1] << 8));
        _offset += 2;
        return value;
    }

    /// <summary>
    /// Reads an unsigned LEB128 varint. Each byte stores 7 data bits;
    /// bit 7 is the continuation flag.
    /// </summary>
    public ulong ReadVarInt()
    {
        ulong result = 0;
        var shift = 0;
        byte b;
        do
        {
            if (_offset >= _data.Length)
                throw new InvalidOperationException("unexpected end of buffer");
            b = _data[_offset++];
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        return result;
    }

    public byte[] ReadVarSlice()
    {
        var length = (int)ReadVarInt();
        return ReadSlice(length);
    }
}
