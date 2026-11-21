using System.Buffers.Binary;
using System.Text;

namespace NWmi.Wmio;

internal sealed class WmioReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public WmioReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public int Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _data.Length)
                throw new WmioFormatException($"Position {value} is outside the {_data.Length}-byte buffer.");
            _position = value;
        }
    }

    public int Length => _data.Length;

    internal byte[] ToArray() => _data.ToArray();

    public int Remaining => _data.Length - _position;

    public bool IsAtEnd => _position == _data.Length;

    public byte ReadByte() => Take(1)[0];

    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public float ReadReal32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public double ReadReal64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>BOOL (MS-WMIO 2.2.75)</summary>
    public bool ReadBool()
    {
        var value = ReadUInt16();
        return value switch
        {
            0x0000 => false,
            0xFFFF => true,
            _ => throw new WmioFormatException($"A BOOL must be 0x0000 or 0xFFFF, not 0x{value:X4}."),
        };
    }

    /// <summary>EncodingLength (MS-WMIO 2.2.73)</summary>
    public int ReadEncodingLength()
    {
        var length = ReadUInt32();
        if (length is < 4 or > int.MaxValue)
            throw new WmioFormatException($"An EncodingLength of {length} is not valid (it must be at least 4).");

        return (int)length;
    }

    /// <summary>HeapLength (MS-WMIO 2.2.66)</summary>
    public int ReadHeapLength()
    {
        var raw = ReadUInt32();
        if ((raw & WmioHeap.LengthMarker) == 0)
            throw new WmioFormatException($"A HeapLength value (0x{raw:X8}) must have its top bit set.");

        return (int)(raw & WmioHeap.LengthMask);
    }

    public WmioReader ReadLengthPrefixedBlock()
    {
        var start = _position;
        var length = ReadEncodingLength();
        if (length - 4 > Remaining)
            throw new WmioFormatException(
                $"A length-prefixed block claims {length} octets but only {Remaining + 4} remain.");

        _position = start + length;
        return new WmioReader(_data.Slice(start, length));
    }

    /// <summary>Encoded-String (MS-WMIO 2.2.78)</summary>
    public string ReadEncodedString()
    {
        var flag = ReadByte();
        switch (flag)
        {
            case 0x00:
            {
                var start = _position;
                var terminator = FindByte(0x00, start);
                var text = Encoding.Latin1.GetString(Take(terminator - start));
                ReadByte(); // the null terminator
                return text;
            }

            case 0x01:
            {
                var start = _position;
                var terminator = FindUtf16Null(start);
                var text = Encoding.Unicode.GetString(Take(terminator - start));
                ReadUInt16(); // the null terminator
                return text;
            }

            default:
                throw new WmioFormatException($"An Encoded-String flag must be 0x00 or 0x01, not 0x{flag:X2}.");
        }
    }

    public byte[] ReadBytes(int count)
    {
        EnsureCount(count, 1);
        return [.. Take(count)];
    }

    public void EnsureCount(long count, int minElementSize)
    {
        if (count < 0 || count * minElementSize > Remaining)
            throw new WmioFormatException($"Count {count} exceeds the {Remaining} bytes remaining.");
    }

    public void Expect(uint expected, string fieldName)
    {
        var actual = ReadUInt32();
        if (actual != expected)
            throw new WmioFormatException($"Expected {fieldName} to be 0x{expected:X8} but found 0x{actual:X8}.");
    }

    private int FindByte(byte value, int start)
    {
        var span = _data.Span;
        for (var i = start; i < span.Length; i++)
            if (span[i] == value)
                return i;

        throw new WmioFormatException("An Encoded-String is missing its null terminator.");
    }

    private int FindUtf16Null(int start)
    {
        var span = _data.Span;
        var i = start;
        while (i + 1 < span.Length)
        {
            if (span[i] == 0 && span[i + 1] == 0)
                return i;

            i += 2;
        }

        throw new WmioFormatException("An Encoded-String is missing its null terminator.");
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
            throw new WmioFormatException($"Need {count} bytes at offset {_position} but only {Remaining} remain.");

        var span = _data.Span.Slice(_position, count);
        _position += count;
        return span;
    }
}
