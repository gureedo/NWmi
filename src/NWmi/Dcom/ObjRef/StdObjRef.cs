using System.Buffers.Binary;
using NWmi.Ndr;

namespace NWmi.Dcom;

/// <summary>The part of an object reference that identifies the object (MS-DCOM 2.2.18.1): 40 bytes on the wire.</summary>
/// <param name="Flags">0, or SORF_NOPING (0x1000) when the client need not ping for this object.</param>
/// <param name="PublicRefs">Public references the client now owns and must release later.</param>
/// <param name="Oxid">Identifies the object exporter that hosts the object.</param>
/// <param name="Oid">Identifies the object for garbage collection.</param>
/// <param name="Ipid">Identifies the interface pointer; sent as the object UUID of calls to it.</param>
internal readonly record struct StdObjRef(uint Flags, uint PublicRefs, ulong Oxid, ulong Oid, Guid Ipid)
{
    public const int Size = 40;
    public const uint NoPing = 0x00001000;

    public static StdObjRef Read(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(data),
        BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
        BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
        BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
        new Guid(data.Slice(24, 16)));

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], PublicRefs);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Oxid);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], Oid);
        Ipid.TryWriteBytes(destination[24..], bigEndian: false, out _);
    }

    /// <summary>
    /// Reads the NDR form (MS-DCOM 2.2.18.3), as it appears inside RemUnknown calls. The structure holds 64-bit members,
    /// so it starts on an 8-byte boundary; in an OBJREF the same fields are byte-packed instead.
    /// </summary>
    public static StdObjRef ReadNdr(NdrReader reader)
    {
        reader.Align(8);
        var flags = reader.ReadUInt32();
        var publicRefs = reader.ReadUInt32();
        var oxid = reader.ReadUInt64();
        var oid = reader.ReadUInt64();
        var ipid = reader.ReadGuid();
        return new StdObjRef(flags, publicRefs, oxid, oid, ipid);
    }

    public void WriteNdr(NdrWriter writer)
    {
        writer.Align(8);
        writer.WriteUInt32(Flags);
        writer.WriteUInt32(PublicRefs);
        writer.WriteUInt64(Oxid);
        writer.WriteUInt64(Oid);
        writer.WriteGuid(Ipid);
    }
}
