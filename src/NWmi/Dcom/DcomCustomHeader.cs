using NWmi.Ndr;

namespace NWmi.Dcom;

/// <summary>
/// CustomHeader (MS-DCOM 2.2.22.1): identifies, by CLSID, the format and size of each property that follows
/// it in an activation properties BLOB. Marshaled with <see cref="TypeSerialization1"/>.
/// </summary>
internal sealed class DcomCustomHeader
{
    public DcomCustomHeader(IReadOnlyList<Guid> propertyClsids, IReadOnlyList<uint> propertySizes)
    {
        ArgumentNullException.ThrowIfNull(propertyClsids);
        ArgumentNullException.ThrowIfNull(propertySizes);
        if (propertyClsids.Count == 0 || propertyClsids.Count != propertySizes.Count)
            throw new ArgumentException("There must be one size for each property, and at least one property.");

        PropertyClsids = propertyClsids;
        PropertySizes = propertySizes;
    }

    /// <summary>The CLSID of each entry in the property array that follows this header, in order.</summary>
    public IReadOnlyList<Guid> PropertyClsids { get; }

    /// <summary>The marshaled size in bytes of each corresponding property.</summary>
    public IReadOnlyList<uint> PropertySizes { get; }

    /// <summary>MSHCTX_DIFFERENTMACHINE, what Windows clients put in destCtx (MS-DCOM 2.2.22.1, footnote &lt;22&gt;).</summary>
    private const uint DestinationDifferentMachine = 2;

    /// <summary>
    /// NDR-encodes the header. <paramref name="totalSize"/> is the size of the whole BLOB (this header plus every
    /// property) and <paramref name="headerSize"/> the size of this header once type-serialization framed; the
    /// caller computes both, since neither changes the length of the encoding.
    /// </summary>
    /// <remarks>
    /// A Windows server uses headerSize to find the first property, so it must be the framed size
    /// (Windows sends 192 for a 176-byte header); with only the inline part, activation fails with
    /// RPC_X_WRONG_ES_VERSION because the server reads a property's framing from the wrong offset.
    /// </remarks>
    internal byte[] Encode(uint totalSize, uint headerSize)
    {
        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteUInt32(totalSize);
            w.WriteUInt32(headerSize);
            w.WriteUInt32(0); // dwReserved
            w.WriteUInt32(DestinationDifferentMachine); // destCtx
            w.WriteUInt32((uint)PropertyClsids.Count);
            w.WriteGuid(Guid.Empty); // classInfoClsid
            w.WriteUniquePointer(true, pw =>
            {
                pw.WriteMaxCount(PropertyClsids.Count);
                foreach (var clsid in PropertyClsids)
                    pw.WriteGuid(clsid);
            });
            w.WriteUniquePointer(true, pw =>
            {
                pw.WriteMaxCount(PropertySizes.Count);
                foreach (var size in PropertySizes)
                    pw.WriteUInt32(size);
            });
            w.WriteUniquePointer(false, null); // pdwReserved
        });

        return writer.ToArray();
    }

    internal static DcomCustomHeader Decode(ReadOnlySpan<byte> data)
    {
        var reader = new NdrReader(data.ToArray());
        List<Guid>? clsids = null;
        List<uint>? sizes = null;
        reader.Struct(r =>
        {
            r.ReadUInt32(); // totalSize: the caller already used this to find the BLOB's extent
            r.ReadUInt32(); // headerSize: informational
            r.ReadUInt32(); // dwReserved
            r.ReadUInt32(); // destCtx
            var count = r.ReadUInt32();
            if (count is 0 or > 0x10000)
                throw new NdrFormatException($"An activation properties header announces {count} properties.");

            r.ReadGuid(); // classInfoClsid
            r.ReadUniquePointer(pr => clsids = ReadGuidArray(pr, count));
            r.ReadUniquePointer(pr => sizes = ReadUInt32Array(pr, count));
            r.ReadUniquePointer(pr => pr.ReadUInt32());
            return 0;
        });

        if (clsids is null || sizes is null)
            throw new NdrFormatException("An activation properties header is missing its property arrays.");

        return new DcomCustomHeader(clsids, sizes);
    }

    private static List<Guid> ReadGuidArray(NdrReader reader, uint count)
    {
        var actual = reader.ReadMaxCount();
        if (actual != count)
            throw new NdrFormatException($"The CLSID array has {actual} entries but cIfs says {count}.");

        reader.EnsureCount(count, 16);
        var result = new List<Guid>((int)count);
        for (var i = 0; i < count; i++)
            result.Add(reader.ReadGuid());

        return result;
    }

    private static List<uint> ReadUInt32Array(NdrReader reader, uint count)
    {
        var actual = reader.ReadMaxCount();
        if (actual != count)
            throw new NdrFormatException($"The size array has {actual} entries but cIfs says {count}.");

        reader.EnsureCount(count, 4);
        var result = new List<uint>((int)count);
        for (var i = 0; i < count; i++)
            result.Add(reader.ReadUInt32());

        return result;
    }
}
