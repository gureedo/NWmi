using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace NWmi.Wmio;

internal sealed class WmioWriter
{
    private readonly ArrayBufferWriter<byte> _buffer;

    public WmioWriter(int initialCapacity = 256)
    {
        _buffer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int Position => _buffer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public byte[] ToArray() => [.. WrittenSpan];

    public void WriteByte(byte value) => Reserve(1)[0] = value;

    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    public void WriteReal32(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);

    public void WriteReal64(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);

    /// <summary>BOOL (MS-WMIO 2.2.75)</summary>
    public void WriteBool(bool value) => WriteUInt16(value ? (ushort)0xFFFF : (ushort)0x0000);

    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Reserve(value.Length));

    public int ReserveUInt32()
    {
        var position = Position;
        WriteUInt32(0);
        return position;
    }

    public void PatchUInt32(int offset, uint value)
    {
        if (offset < 0 || offset > Position - sizeof(uint))
            throw new ArgumentOutOfRangeException(nameof(offset));

        BinaryPrimitives.WriteUInt32LittleEndian(
            MemoryMarshal.AsMemory(_buffer.WrittenMemory).Span.Slice(offset, sizeof(uint)), value);
    }

    public void WriteLengthPrefixedBlock(Action<WmioWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var start = Position;
        var lengthPosition = ReserveUInt32();
        body(this);
        PatchUInt32(lengthPosition, (uint)(Position - start));
    }

    /// <summary>HeapLength (MS-WMIO 2.2.66)</summary>
    public void WriteHeapLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        WriteUInt32((uint)length | WmioHeap.LengthMarker);
    }

    /// <summary>Encoded-String (MS-WMIO 2.2.78)</summary>
    public void WriteEncodedString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (CanCompress(value))
        {
            WriteByte(0x00);
            foreach (var c in value)
                WriteByte((byte)c);
            WriteByte(0x00);
        }
        else
        {
            WriteByte(0x01);
            WriteBytes(Encoding.Unicode.GetBytes(value));
            WriteUInt16(0x0000);
        }
    }

    public static int EncodedStringLength(string value) =>
        CanCompress(value) ? value.Length + 2 : (value.Length + 1) * 2 + 1;

    private static bool CanCompress(string value) => value.All(c => c <= 0x00FF);

    private Span<byte> Reserve(int count)
    {
        var span = _buffer.GetSpan(count)[..count];
        _buffer.Advance(count);
        return span;
    }
}
