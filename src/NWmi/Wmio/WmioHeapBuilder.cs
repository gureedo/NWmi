namespace NWmi.Wmio;

/// <summary>
/// Builds a Heap by appending items and handing back the reference to use for each one. Strings that match
/// the common-string dictionary (MS-WMIO 2.2.80) are not stored at all; their reference just names the ordinal.
/// </summary>
internal sealed class WmioHeapBuilder
{
    private readonly WmioWriter _writer = new();

    /// <summary>Adds a string (or returns the null reference for a null string), preferring a dictionary reference when one matches.</summary>
    public uint AddStringRef(string? value)
    {
        if (value is null)
            return WmioHeap.NullRef;

        var ordinal = WmioDictionary.IndexOf(value);
        if (ordinal >= 0)
            return WmioHeap.DictionaryFlag | (uint)ordinal;

        var offset = (uint)_writer.Position;
        _writer.WriteEncodedString(value);
        return offset;
    }

    /// <summary>
    /// Appends an opaque item (PropertyInfo, a QualifierSet, an Encoded-Array, an ObjectBlock, ...) and returns
    /// its HeapRef. <paramref name="body"/> writes into a temporary buffer rather than the heap directly: if it
    /// makes further <see cref="AddStringRef"/>/<see cref="AddItem"/> calls of its own (an array's string
    /// elements, a PropertyInfo's qualifier values, ...), those must land at the heap's true, current tail, not
    /// in the middle of this item's own still-unfinished bytes. Building into a scratch buffer and appending
    /// that only once <paramref name="body"/> returns puts every nested item ahead of this one in heap order
    /// (harmless; MS-WMIO 2.2.66 says items may appear in any order) while keeping each item's own bytes
    /// contiguous and this item's returned offset correct, however deeply such calls nest.
    /// </summary>
    public uint AddItem(Action<WmioWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var scratch = new WmioWriter();
        body(scratch);
        var offset = (uint)_writer.Position;
        _writer.WriteBytes(scratch.WrittenSpan);
        return offset;
    }

    /// <summary>Writes the finished Heap (its HeapLength, then every appended item) into <paramref name="writer"/>.</summary>
    public void WriteTo(WmioWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteHeapLength(_writer.Position);
        writer.WriteBytes(_writer.WrittenSpan);
    }
}
