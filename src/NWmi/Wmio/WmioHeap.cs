namespace NWmi.Wmio;

/// <summary>
/// A decoded Heap (MS-WMIO 2.2.66): a byte buffer that HeapRef values (MS-WMIO 2.2.69) index into. ClassHeap,
/// InstanceHeap and MethodHeap are each a separate instance; a reference from one is never valid in another.
/// </summary>
internal sealed class WmioHeap
{
    /// <summary>The bit every HeapLength has set (MS-WMIO 2.2.66: "31 bits with MS bit set"); not the dictionary flag of a HeapRef.</summary>
    internal const uint LengthMarker = 0x80000000;

    /// <summary>The bits of a HeapLength that hold the length in octets (MS-WMIO 2.2.66).</summary>
    internal const uint LengthMask = 0x7FFFFFFF;

    /// <summary>The HeapRef that names nothing (MS-WMIO 2.2.69).</summary>
    internal const uint NullRef = 0xFFFFFFFF;
    /// <summary>Set in a HeapStringRef that names an entry of the common-string dictionary instead of a heap offset (MS-WMIO 2.2.80).</summary>
    internal const uint DictionaryFlag = 0x80000000;
    /// <summary>The bits of a HeapRef that hold the heap offset, or the dictionary ordinal when <see cref="DictionaryFlag"/> is set.</summary>
    internal const uint OffsetMask = 0x7FFFFFFF;

    private readonly byte[] _bytes;

    private WmioHeap(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>Reads a Heap: its HeapLength, then exactly that many bytes, advancing past them.</summary>
    public static WmioHeap Read(WmioReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var length = reader.ReadHeapLength();
        return new WmioHeap(reader.ReadBytes(length));
    }

    /// <summary>True when <paramref name="rawHeapRef"/> is 0xFFFFFFFF, meaning the item is absent (MS-WMIO 2.2.69).</summary>
    public static bool IsNull(uint rawHeapRef) => rawHeapRef == NullRef;

    /// <summary>A reader positioned at <paramref name="offset"/> within this heap, for a HeapItem the caller decodes itself.</summary>
    public WmioReader At(int offset)
    {
        var reader = new WmioReader(_bytes) { Position = offset };
        return reader;
    }

    /// <summary>
    /// Resolves a HeapRef that must be a plain offset: PropertyInfoRef, HeapQualifierSetRef, HeapRef to an
    /// array or embedded object. These are never dictionary references (MS-WMIO 2.2.80 only applies to strings).
    /// </summary>
    public WmioReader Resolve(uint rawHeapRef)
    {
        if (IsNull(rawHeapRef))
            throw new WmioFormatException("A required HeapRef is null.");

        if ((rawHeapRef & DictionaryFlag) != 0)
            throw new WmioFormatException(
                $"HeapRef 0x{rawHeapRef:X8} has the dictionary bit set, but it does not point to a string.");

        return At((int)(rawHeapRef & OffsetMask));
    }

    /// <summary>Resolves a HeapStringRef (MS-WMIO 2.2.68): dictionary ordinal, heap offset, or null.</summary>
    public string? ResolveString(uint rawHeapRef)
    {
        if (IsNull(rawHeapRef))
            return null;

        if ((rawHeapRef & DictionaryFlag) != 0)
            return WmioDictionary.Get((int)(rawHeapRef & OffsetMask));

        return At((int)(rawHeapRef & OffsetMask)).ReadEncodedString();
    }

    public string ResolveRequiredString(uint rawHeapRef) =>
        ResolveString(rawHeapRef) ?? throw new WmioFormatException("A required HeapStringRef is null.");
}
