namespace NWmi.Wmio;

/// <summary>NdTable (MS-WMIO 2.2.26): two bits per property, packed LSB-first into octets, indexed by the property's DeclarationOrder.</summary>
internal static class NdTable
{
    public readonly record struct Entry(bool IsNull, bool IsInherited);

    public static int OctetCount(int propertyCount) => propertyCount == 0 ? 0 : ((propertyCount - 1) / 4) + 1;

    public static Entry ReadEntry(ReadOnlySpan<byte> table, int index, int propertyCount, string propertyName)
    {
        if (index >= propertyCount)
            throw new WmioFormatException(
                $"Property '{propertyName}' has NdTable index {index} but the class has only {propertyCount} properties.");

        return Read(table, index);
    }

    /// <summary>
    /// True when the declaration orders are exactly 0..n-1, which is when they can index the NdTable. A class built in code
    /// whose orders are all zero, say, has no meaningful order and uses the list order instead; the reader and the writer
    /// both apply this rule.
    /// </summary>
    public static bool IsDeclarationOrdered(IEnumerable<int> declarationOrders, int propertyCount) =>
        declarationOrders.Order().SequenceEqual(Enumerable.Range(0, propertyCount));

    public static int PeekDeclarationOrder(WmioHeap classHeap, uint propertyInfoRef)
    {
        var info = classHeap.Resolve(propertyInfoRef);
        info.ReadUInt32(); // PropertyType
        return info.ReadUInt16();
    }

    public static Entry Read(ReadOnlySpan<byte> table, int propertyIndex)
    {
        var octet = table[propertyIndex / 4];
        var shift = (propertyIndex % 4) * 2;
        return new Entry(((octet >> shift) & 1) != 0, ((octet >> (shift + 1)) & 1) != 0);
    }

    /// <summary>
    /// One entry per property, laid out by DeclarationOrder. A class built in code whose declaration orders are not a
    /// permutation of 0..n-1 (all zero, say) has no meaningful order, so it falls back to the list order.
    /// </summary>
    public static IReadOnlyList<Entry> InDeclarationOrder(IReadOnlyList<WmioProperty> properties,
        Func<WmioProperty, Entry> entryOf)
    {
        var orders = properties.Select(p => (int)p.DeclarationOrder).ToList();
        var isPermutation = IsDeclarationOrdered(orders, properties.Count);
        var entries = new Entry[properties.Count];
        for (var i = 0; i < properties.Count; i++)
            entries[isPermutation ? orders[i] : i] = entryOf(properties[i]);

        return entries;
    }

    public static byte[] Write(IReadOnlyList<Entry> entries)
    {
        var table = new byte[OctetCount(entries.Count)];
        for (var i = 0; i < entries.Count; i++)
        {
            var shift = (i % 4) * 2;
            byte bits = 0;
            if (entries[i].IsNull)
                bits |= 1;

            if (entries[i].IsInherited)
                bits |= 2;

            table[i / 4] |= (byte)(bits << shift);
        }

        return table;
    }
}
