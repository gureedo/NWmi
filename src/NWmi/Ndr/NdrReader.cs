using System.Buffers.Binary;
using System.Text;

namespace NWmi.Ndr;

/// <summary>
/// NDR 2.0 deserializer, little-endian only, mirroring <see cref="NdrWriter"/>. Alignment is relative to the
/// first byte of the buffer. Every length is checked against the bytes remaining before anything is allocated.
/// </summary>
internal sealed class NdrReader(ReadOnlyMemory<byte> data)
{
    public const int MaxNesting = 64;

    private int _depth;
    private int _scopes;
    private List<Action<NdrReader>>? _deferred;

    public int Position { get; private set; }

    public int Remaining => data.Length - Position;

    public bool IsAtEnd => Position == data.Length;

    /// <summary>Skips padding up to a multiple of <paramref name="alignment"/> (a power of two).</summary>
    public void Align(int alignment)
    {
        var pad = -Position & (alignment - 1);
        Take(pad);
    }

    public byte ReadByte() => Take(1)[0];

    public bool ReadBoolean() => ReadByte() != 0;

    public ushort ReadUInt16()
    {
        Align(2);
        return BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    }

    public short ReadInt16() => unchecked((short)ReadUInt16());

    public uint ReadUInt32()
    {
        Align(4);
        return BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    }

    public int ReadInt32() => unchecked((int)ReadUInt32());

    public ulong ReadUInt64()
    {
        Align(8);
        return BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    }

    public long ReadInt64() => unchecked((long)ReadUInt64());

    public float ReadSingle()
    {
        Align(4);
        return BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    }

    public double ReadDouble()
    {
        Align(8);
        return BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    }

    public Guid ReadGuid()
    {
        Align(4);
        return new Guid(Take(16), bigEndian: false);
    }

    public byte[] ReadBytes(int count)
    {
        EnsureCount(count, 1);
        return [.. Take(count)];
    }

    /// <summary>Reads a conformance <c>max_count</c>, rejecting values that cannot be a valid length.</summary>
    public int ReadMaxCount()
    {
        var count = ReadUInt32();
        if (count > int.MaxValue)
            throw new NdrFormatException($"Array count {count} is too large.");

        return (int)count;
    }

    public byte[] ReadConformantBytes()
    {
        var count = ReadMaxCount();
        return ReadBytes(count);
    }

    /// <summary>A <c>[string] wchar_t[]</c>; the terminating NUL, if present, is removed.</summary>
    public string ReadConformantVaryingString()
    {
        var max = ReadMaxCount();
        var offset = ReadUInt32();
        var actual = ReadUInt32();
        if (offset > max || actual > max - offset)
            throw new NdrFormatException($"String bounds are inconsistent (max {max}, offset {offset}, actual {actual}).");

        EnsureCount(actual, 2);
        var bytes = Take(checked((int)actual * 2));
        var length = (int)actual;
        if (length > 0 && bytes[^1] == 0 && bytes[^2] == 0)
            length--;

        return Encoding.Unicode.GetString(bytes[..(length * 2)]);
    }

    public void EnsureCount(long count, int minElementSize)
    {
        if (count < 0 || count * minElementSize > Remaining)
            throw new NdrFormatException($"Count {count} exceeds the {Remaining} bytes remaining.");
    }

    public T Struct<T>(Func<NdrReader, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_depth > 0)
            return body(this);

        if (++_scopes > MaxNesting)
            throw new NdrFormatException($"Structures are nested more than {MaxNesting} levels deep.");

        _depth = 1;
        T result;
        try
        {
            result = body(this);
        }
        finally
        {
            _depth = 0;
        }

        var items = _deferred;
        _deferred = null;
        if (items is not null)
            foreach (var item in items)
                item(this);

        _scopes--;
        return result;
    }

    /// <summary>A <c>[unique]</c> pointer; a zero referent id gives a resolved null result.</summary>
    public NdrDeferred<T> ReadUniquePointer<T>(Func<NdrReader, T> body)
    {
        var referent = ReadUInt32();
        var result = new NdrDeferred<T>(referent);
        if (referent == 0)
        {
            result.Resolve(default!);
            return result;
        }

        ReadPointee(result, body);
        return result;
    }

    /// <summary>
    /// A <c>[ref]</c> pointer. Top-level it is just the pointee; embedded in a structure it carries a
    /// referent id that must not be zero.
    /// </summary>
    public NdrDeferred<T> ReadReferencePointer<T>(Func<NdrReader, T> body)
    {
        var referent = 0u;
        if (_depth > 0)
        {
            referent = ReadUInt32();
            if (referent == 0)
                throw new NdrFormatException("A reference pointer must not be null.");
        }

        var result = new NdrDeferred<T>(referent);
        ReadPointee(result, body);
        return result;
    }

    private void ReadPointee<T>(NdrDeferred<T> result, Func<NdrReader, T> body)
    {
        if (_depth > 0)
            (_deferred ??= []).Add(Read);
        else
            Read(this);
        return;

        void Read(NdrReader r) => result.Resolve(r.Struct(body));
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Remaining)
            throw new NdrFormatException($"Need {count} bytes at offset {Position} but only {Remaining} remain.");

        var span = data.Span.Slice(Position, count);
        Position += count;
        return span;
    }
}
