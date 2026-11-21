using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace NWmi.Ndr;

/// <summary>
/// NDR 2.0 (transfer syntax 8a885d04-1ceb-11c9-9fe8-08002b104860) serializer, little-endian only.
/// Alignment is relative to the first byte written, which must be the first byte of the stub data.
/// </summary>
/// <remarks>
/// Pointer referents inside a structure are deferred: the referent id is written in place and the
/// pointee is written after the outermost enclosing structure ends, depth-first (C706 14.3.12).
/// Wrap every structure that can contain pointers in <see cref="Struct"/>. Full pointers (aliasing) are not supported.
/// </remarks>
internal sealed class NdrWriter(int initialCapacity = 256)
{
    /// <summary>First referent id handed out; this is what Windows RPC uses.</summary>
    public const uint FirstReferentId = 0x00020000;

    private readonly ArrayBufferWriter<byte> _buffer = new(Math.Max(initialCapacity, 16));
    private int _depth;
    private List<Action<NdrWriter>>? _deferred;
    private uint _nextReferentId = FirstReferentId;

    public int Position => _buffer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public byte[] ToArray() => [.. WrittenSpan];

    /// <summary>Pads with zero bytes to a multiple of <paramref name="alignment"/> (a power of two).</summary>
    public void Align(int alignment)
    {
        var pad = -Position & (alignment - 1);
        if (pad != 0)
            Reserve(pad).Clear();
    }

    public void WriteByte(byte value) => Reserve(1)[0] = value;

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        Align(2);
        BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
    }

    public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

    public void WriteUInt32(uint value)
    {
        Align(4);
        BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
    }

    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public void WriteUInt64(ulong value)
    {
        Align(8);
        BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);
    }

    public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

    public void WriteSingle(float value)
    {
        Align(4);
        BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);
    }

    public void WriteDouble(double value)
    {
        Align(8);
        BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);
    }

    /// <summary>MS-DTYP 2.3.4.2</summary>
    public void WriteGuid(Guid value)
    {
        Align(4);
        value.TryWriteBytes(Reserve(16), bigEndian: false, out _);
    }

    public void WriteEnum16(int value) => WriteUInt16(checked((ushort)value));

    public void WriteEnum32(int value) => WriteInt32(value);

    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Reserve(value.Length));

    /// <summary>The <c>max_count</c> of a conformant array (also hoisted to the start of a conformant structure).</summary>
    public void WriteMaxCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        WriteUInt32((uint)count);
    }

    /// <summary>Conformant array of bytes: max_count then the data.</summary>
    public void WriteConformantBytes(ReadOnlySpan<byte> value)
    {
        WriteMaxCount(value.Length);
        WriteBytes(value);
    }

    /// <summary>
    /// A <c>[string] wchar_t[]</c>: conformant varying with max_count = actual_count = length + 1,
    /// offset 0, terminated by a NUL (C706 14.3.4.3).
    /// </summary>
    public void WriteConformantVaryingString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = value.Length + 1;
        WriteMaxCount(count);
        WriteUInt32(0);
        WriteUInt32((uint)count);
        Encoding.Unicode.GetBytes(value, Reserve(value.Length * 2));
        WriteUInt16(0);
    }

    /// <summary>
    /// Runs <paramref name="body"/> as one structure. Pointees of embedded pointers are written when the
    /// outermost structure completes. Nested calls just run the body.
    /// </summary>
    public void Struct(Action<NdrWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_depth > 0)
        {
            body(this);
            return;
        }

        _depth = 1;
        try
        {
            body(this);
        }
        finally
        {
            _depth = 0;
        }

        var items = _deferred;
        _deferred = null;
        if (items is null)
            return;

        foreach (var item in items)
            item(this);
    }

    /// <summary>
    /// A <c>[unique]</c> pointer: a referent id (0 when <paramref name="present"/> is false) and, when present,
    /// the pointee written by <paramref name="body"/> (deferred when embedded in a structure).
    /// </summary>
    public void WriteUniquePointer(bool present, Action<NdrWriter>? body)
    {
        WriteUInt32(present ? NextReferentId() : 0);
        if (present)
            WritePointee(body);
    }

    public void WriteUniquePointer<T>(T? value, Action<NdrWriter, T> body)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(body);
        WriteUniquePointer(value is not null, value is null ? null : w => body(w, value));
    }

    /// <summary>
    /// A <c>[ref]</c> pointer. Top-level it is just the pointee; embedded in a structure it carries a
    /// non-null referent id and the pointee is deferred (C706 14.3.10; MS-RPCE 2.2.5.3.5.1).
    /// NDR's special case for arrays whose elements are reference pointers is not implemented.
    /// </summary>
    public void WriteReferencePointer(Action<NdrWriter> body)
    {
        if (_depth > 0)
            WriteUInt32(NextReferentId());
        WritePointee(body);
    }

    private void WritePointee(Action<NdrWriter>? body)
    {
        if (_depth > 0)
            (_deferred ??= []).Add(Write);
        else
            Write(this);

        return;

        void Write(NdrWriter w) => w.Struct(body ?? (_ => { }));
    }

    internal void PatchUInt16(int position, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(WrittenSlice(position, sizeof(ushort)), value);

    internal void PatchUInt32(int position, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(WrittenSlice(position, sizeof(uint)), value);

    /// <summary>
    /// A writable view of bytes that were already written. <see cref="ArrayBufferWriter{T}"/> only exposes its written
    /// region read-only, so this goes through <see cref="MemoryMarshal.AsMemory{T}(ReadOnlyMemory{T})"/>; the memory is
    /// the writer's own mutable backing array.
    /// </summary>
    private Span<byte> WrittenSlice(int position, int count)
    {
        if (position < 0 || position > Position - count)
            throw new ArgumentOutOfRangeException(nameof(position));

        return MemoryMarshal.AsMemory(_buffer.WrittenMemory).Span.Slice(position, count);
    }

    private uint NextReferentId()
    {
        var id = _nextReferentId;
        _nextReferentId += 4;
        return id;
    }

    private Span<byte> Reserve(int count)
    {
        var span = _buffer.GetSpan(count)[..count];
        _buffer.Advance(count);
        return span;
    }
}
