using NWmi.Ndr;

namespace NWmi.Dcom;

/// <summary>One item of out-of-band data attached to a call or its reply (MS-DCOM 2.2.13.1). Unknown ones are ignored.</summary>
internal sealed record OrpcExtent(Guid Id, byte[] Data);

/// <summary>What the server put in front of the results of a call (ORPCTHAT, MS-DCOM 2.2.13.4).</summary>
internal sealed record OrpcThat(uint Flags, IReadOnlyList<OrpcExtent> Extensions);

/// <summary>
/// The implicit first argument of every DCOM call (ORPCTHIS) and the first value of every reply (ORPCTHAT).
/// Both are written and read as the beginning of the NDR stub data.
/// </summary>
internal static class OrpcHeader
{
    /// <summary>Writes ORPCTHIS with no extensions: version, flags, reserved, causality id, null extension pointer.</summary>
    /// <param name="writer">Positioned at the start of the stub data.</param>
    /// <param name="causalityId">Identifies the logical thread of calls this call belongs to (MS-DCOM 1.3.5).</param>
    /// <param name="version">The DCOM version to announce; <see cref="DcomConstants.ClientVersion"/> by default.</param>
    public static void WriteThis(NdrWriter writer, Guid causalityId, Version? version = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var v = version ?? DcomConstants.ClientVersion;
        writer.Struct(w =>
        {
            w.WriteUInt16((ushort)v.Major);
            w.WriteUInt16((ushort)v.Minor);
            w.WriteUInt32(0); // flags: zero for ORPC invocations
            w.WriteUInt32(0); // reserved1
            w.WriteGuid(causalityId);
            w.WriteUniquePointer(false, null); // extensions
        });
    }

    /// <summary>Reads ORPCTHAT, including (and discarding the meaning of) any extensions, so the reader ends up at the results.</summary>
    public static OrpcThat ReadThat(NdrReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        NdrDeferred<IReadOnlyList<OrpcExtent>>? extensions = null;
        uint flags = 0;
        reader.Struct(r =>
        {
            flags = r.ReadUInt32();
            extensions = r.ReadUniquePointer(ReadExtentArray);
            return 0;
        });

        return new OrpcThat(flags, extensions!.IsNull ? [] : extensions.Value);
    }

    /// <summary>ORPC_EXTENT_ARRAY: size, reserved, and a pointer to an array of pointers to extents (MS-DCOM 2.2.13.2).</summary>
    private static IReadOnlyList<OrpcExtent> ReadExtentArray(NdrReader reader)
    {
        var size = reader.ReadUInt32();
        reader.ReadUInt32(); // reserved
        var pointers = reader.ReadUniquePointer(r => ReadExtentPointers(r, size));

        var extents = new List<OrpcExtent>();
        pointers.Then(list =>
        {
            foreach (var item in list)
                item.Then(extent =>
                {
                    if (extent is not null)
                        extents.Add(extent);
                });
        });
        return extents;
    }

    /// <summary>The array holds (size + 1) &amp; ~1 pointers, so there may be one null padding entry.</summary>
    private static List<NdrDeferred<OrpcExtent?>> ReadExtentPointers(NdrReader reader, uint size)
    {
        var expected = (size + 1) & ~1u;
        var count = reader.ReadMaxCount();
        if (count != expected)
            throw new NdrFormatException($"An ORPC extent array of size {size} has {count} entries instead of {expected}.");

        reader.EnsureCount(count, 4);
        var items = new List<NdrDeferred<OrpcExtent?>>(count);
        for (var i = 0; i < count; i++)
            items.Add(reader.ReadUniquePointer<OrpcExtent?>(ReadExtent));

        return items;
    }

    /// <summary>ORPC_EXTENT: a GUID, the data size, and the data padded to a multiple of 8 bytes (MS-DCOM 2.2.13.1).</summary>
    private static OrpcExtent ReadExtent(NdrReader reader)
    {
        var maxCount = reader.ReadMaxCount(); // hoisted conformance of the trailing array
        var id = reader.ReadGuid();
        var size = reader.ReadUInt32();
        var padded = (size + 7) & ~7u;
        if (size > maxCount || maxCount != padded)
            throw new NdrFormatException($"An ORPC extent of size {size} has {maxCount} bytes of data instead of {padded}.");

        var data = reader.ReadBytes(maxCount);
        return new OrpcExtent(id, data[..(int)size]);
    }
}
