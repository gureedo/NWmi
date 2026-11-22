namespace NWmi.Wmio;

/// <summary>One CIM qualifier (MS-WMIO 2.2.60): a name, propagation flavor, type, and value.</summary>
internal sealed record WmioQualifier(string Name, QualifierFlavor Flavor, WbemType Type, object? Value)
{
    /// <summary>
    /// Decodes a QualifierSet (MS-WMIO 2.2.59): a length-prefixed block holding zero or more qualifiers back
    /// to back, with no count or delimiters; decoding stops exactly when the block's bytes run out.
    /// </summary>
    public static IReadOnlyList<WmioQualifier> ReadSet(WmioReader reader, WmioHeap heap)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ReadSetFromBlock(reader.ReadLengthPrefixedBlock(), heap);
    }

    /// <summary>
    /// Like <see cref="ReadSet"/>, but <paramref name="block"/> is already the framed QualifierSet reader (as
    /// returned by <see cref="WmioReader.ReadLengthPrefixedBlock"/>), for callers that captured its byte range
    /// earlier and are only now able to resolve heap references within it.
    /// </summary>
    internal static IReadOnlyList<WmioQualifier> ReadSetFromBlock(WmioReader block, WmioHeap heap)
    {
        ArgumentNullException.ThrowIfNull(block);
        block.Position = 0;
        block.ReadUInt32(); // the length itself, already accounted for by the block's own extent
        var qualifiers = new List<WmioQualifier>();
        while (!block.IsAtEnd)
            qualifiers.Add(ReadOne(block, heap));

        return qualifiers;
    }

    /// <summary>Encodes a QualifierSet: its own EncodingLength, then each qualifier in order.</summary>
    public static void WriteSet(WmioWriter writer, IReadOnlyList<WmioQualifier> qualifiers, WmioHeapBuilder heap)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(qualifiers);
        writer.WriteLengthPrefixedBlock(w =>
        {
            foreach (var qualifier in qualifiers)
                qualifier.Write(w, heap);
        });
    }

    private static WmioQualifier ReadOne(WmioReader reader, WmioHeap heap)
    {
        var name = heap.ResolveRequiredString(reader.ReadUInt32());
        var flavor = (QualifierFlavor)reader.ReadByte();
        var type = (WbemType)reader.ReadUInt32();
        var value = WmioValue.ReadInline(reader, type, heap);
        return new WmioQualifier(name, flavor, type, value);
    }

    private void Write(WmioWriter writer, WmioHeapBuilder heap)
    {
        writer.WriteUInt32(heap.AddStringRef(Name));
        writer.WriteByte((byte)Flavor);
        writer.WriteUInt32((uint)Type);
        WmioValue.WriteInline(writer, Type, Value, heap);
    }
}
