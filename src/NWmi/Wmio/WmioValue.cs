namespace NWmi.Wmio;

/// <summary>
/// Reads and writes EncodedValue (MS-WMIO 2.2.71): the inline representation in a ValueTable or qualifier,
/// and the heap-backed representation for strings, arrays and embedded objects. Values are plain .NET objects:
/// <c>sbyte</c>/<c>byte</c>/<c>short</c>/<c>ushort</c>/<c>int</c>/<c>uint</c>/<c>long</c>/<c>ulong</c>/<c>float</c>/<c>double</c>
/// for the numeric base types, <c>bool</c>, <c>char</c> for CHAR16, <c>string</c> for STRING/DATETIME/REFERENCE
/// (the qualifier that distinguishes them is carried separately, as <see cref="WbemType"/>), <c>object?[]</c> for
/// any array, and <see cref="WmioObjectBlock"/> for CIM-TYPE-OBJECT. A value is null exactly when the property
/// or qualifier is NULL (MS-WMIO 2.2.74, or an absent heap reference for a nullable scalar case).
/// </summary>
internal static class WmioValue
{
    /// <summary>
    /// Reads one EncodedValue of <paramref name="type"/> from its inline slot (a ValueTable entry, or right
    /// after a QualifierType). For heap-backed types this resolves through <paramref name="heap"/> immediately.
    /// </summary>
    public static object? ReadInline(WmioReader reader, WbemType type, WmioHeap heap)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(heap);
        if (!type.BaseType().IsSupportedBaseType())
            // Checked here, before IsArray()/InlineValueSize() ever run on it: those assume a recognized
            // base type and throw NotSupportedException for anything else, which untrusted input must never
            // be allowed to trigger (only WmioFormatException is a contract decoders can rely on).
            throw new WmioFormatException($"Unsupported CIM type 0x{(ushort)type:X4}.");

        if (type.IsArray())
        {
            var arrayRef = reader.ReadUInt32();

            // A server may use a plain 0 (not the documented 0xFFFFFFFF null HeapRef) for an
            // empty array's ValueTable slot, with the NdFlag still marking it as a real local value, not
            // null (for example a Win32_NTLogEvent instance's empty Data property): resolving 0 as an
            // ordinary heap offset lands on whatever unrelated HeapItem happens to sit there (here, the
            // class name string), so it must be special-cased before ever calling heap.Resolve, exactly
            // like the documented null sentinel.
            if (WmioHeap.IsNull(arrayRef))
                return null;

            return arrayRef == 0 ? [] : ReadArray(heap.Resolve(arrayRef), type.BaseType(), heap);
        }

        return type.BaseType() switch
        {
            WbemType.Sint8 => reader.ReadSByte(),
            WbemType.Uint8 => reader.ReadByte(),
            WbemType.Sint16 => reader.ReadInt16(),
            WbemType.Uint16 => reader.ReadUInt16(),
            WbemType.Sint32 => reader.ReadInt32(),
            WbemType.Uint32 => reader.ReadUInt32(),
            WbemType.Sint64 => reader.ReadInt64(),
            WbemType.Uint64 => reader.ReadUInt64(),
            WbemType.Real32 => reader.ReadReal32(),
            WbemType.Real64 => reader.ReadReal64(),
            WbemType.Boolean => reader.ReadBool(),
            WbemType.Char16 => (char)reader.ReadUInt16(),
            WbemType.String or WbemType.DateTime or WbemType.Reference => ResolveStringSlot(reader.ReadUInt32(), heap),
            WbemType.Object => ReadObjectRef(reader, heap),
            _ => throw new WmioFormatException($"Cannot read a value of unsupported CIM type {type}."),
        };
    }

    /// <summary>
    /// Writes one EncodedValue inline, exactly mirroring <see cref="ReadInline"/>.
    /// A null value needs <paramref name="type"/> to be nullable on the wire (string, reference, datetime, object, or any array).
    /// </summary>
    public static void WriteInline(WmioWriter writer, WbemType type, object? value, WmioHeapBuilder heap)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(heap);
        if (type.IsArray())
        {
            if (value is null)
            {
                writer.WriteUInt32(WmioHeap.NullRef);
                return;
            }

            var elements = (object?[])value;
            writer.WriteUInt32(heap.AddItem(w => WriteArray(w, type.BaseType(), elements, heap)));
            return;
        }

        switch (type.BaseType())
        {
            case WbemType.Sint8:
                writer.WriteSByte(RequireValue<sbyte>(value, type));
                break;
            case WbemType.Uint8:
                writer.WriteByte(RequireValue<byte>(value, type));
                break;
            case WbemType.Sint16:
                writer.WriteInt16(RequireValue<short>(value, type));
                break;
            case WbemType.Uint16:
                writer.WriteUInt16(RequireValue<ushort>(value, type));
                break;
            case WbemType.Sint32:
                writer.WriteInt32(RequireValue<int>(value, type));
                break;
            case WbemType.Uint32:
                writer.WriteUInt32(RequireValue<uint>(value, type));
                break;
            case WbemType.Sint64:
                writer.WriteInt64(RequireValue<long>(value, type));
                break;
            case WbemType.Uint64:
                writer.WriteUInt64(RequireValue<ulong>(value, type));
                break;
            case WbemType.Real32:
                writer.WriteReal32(RequireValue<float>(value, type));
                break;
            case WbemType.Real64:
                writer.WriteReal64(RequireValue<double>(value, type));
                break;
            case WbemType.Boolean:
                writer.WriteBool(RequireValue<bool>(value, type));
                break;
            case WbemType.Char16:
                writer.WriteUInt16(RequireValue<char>(value, type));
                break;
            case WbemType.String or WbemType.DateTime or WbemType.Reference:
                writer.WriteUInt32(heap.AddStringRef(value is null ? null : RequireValue<string>(value, type)));
                break; // AddStringRef itself writes the null reference (0xFFFFFFFF) when given null
            case WbemType.Object:
                WriteObjectRef(writer, type, value, heap);
                break;
            default:
                throw new NotSupportedException($"Cannot write a value of unsupported CIM type {type}.");
        }
    }

    /// <summary>
    /// The octets a value of <paramref name="type"/> would occupy as NoValue filler (MS-WMIO 2.2.74): the same
    /// width <see cref="WbemTypeExtensions.InlineValueSize"/> reports, every octet 0xFF.
    /// </summary>
    public static void WriteNoValue(WmioWriter writer, WbemType type)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var filler = new byte[type.InlineValueSize()];
        filler.AsSpan().Fill(0xFF);
        writer.WriteBytes(filler);
    }

    /// <summary>
    /// A server may leave a string ValueTable slot at plain 0 for a value that is not set (with the
    /// NdTable saying it has one): resolved as an offset it names the class-name string that happens to sit at
    /// heap offset 0 (e.g. Win32_NTLogEvent's User reading "Win32_NTLogEvent"). Treated as null, like 0xFFFFFFFF.
    /// </summary>
    private static string? ResolveStringSlot(uint reference, WmioHeap heap) =>
        reference == 0 ? null : heap.ResolveString(reference);

    private static T RequireValue<T>(object? value, WbemType type)
    {
        if (value is T typed)
            return typed;

        throw new ArgumentException($"{value?.GetType().Name ?? "null"} is not a valid value for {type}.",
            nameof(value));
    }

    private static WmioObjectBlock? ReadObjectRef(WmioReader reader, WmioHeap heap)
    {
        var objectRef = reader.ReadUInt32();
        if (WmioHeap.IsNull(objectRef))
            return null;

        var objectReader = heap.Resolve(objectRef);

        // Like a MethodSignatureBlock, an embedded object's length prefix is just the ObjectBlock's own byte
        // count, not a self-inclusive EncodingLength.
        var length = objectReader.ReadUInt32();
        if (length > int.MaxValue)
            throw new WmioFormatException($"An embedded object's length of {length} is too large.");

        return WmioObjectBlock.Decode(new WmioReader(objectReader.ReadBytes((int)length)));
    }

    private static void WriteObjectRef(WmioWriter writer, WbemType type, object? value, WmioHeapBuilder heap)
    {
        if (value is null)
        {
            writer.WriteUInt32(WmioHeap.NullRef);
            return;
        }

        var block = RequireValue<WmioObjectBlock>(value, type);
        writer.WriteUInt32(heap.AddItem(w =>
        {
            var body = new WmioWriter();
            block.Encode(body);
            w.WriteUInt32((uint)body.Position);
            w.WriteBytes(body.WrittenSpan);
        }));
    }

    private static object?[] ReadArray(WmioReader reader, WbemType elementType, WmioHeap heap)
    {
        var count = reader.ReadUInt32();
        reader.EnsureCount(count, elementType.InlineValueSize());
        var elements = new object?[count];
        for (var i = 0; i < count; i++)
            elements[i] = ReadInline(reader, elementType, heap);

        return elements;
    }

    private static void WriteArray(WmioWriter writer, WbemType elementType, object?[] elements, WmioHeapBuilder heap)
    {
        writer.WriteUInt32((uint)elements.Length);
        foreach (var element in elements)
            WriteInline(writer, elementType, element, heap);
    }
}
