using System.Buffers.Binary;

namespace NWmi.Dcom;

/// <summary>
/// A marshaled interface pointer (MS-DCOM 2.2.18), the byte array inside an MInterfacePointer.
/// Only the standard, handler and custom forms are supported.
/// </summary>
internal abstract class ObjRef(Guid iid)
{
    private const uint Signature = 0x574f454d; // "MEOW"
    private const int HeaderSize = 24;

    /// <summary>The interface this reference was marshaled for.</summary>
    public Guid Iid { get; } = iid;

    /// <summary>The byte-packed form.</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)Type);
        Iid.TryWriteBytes(header.AsSpan(8), bigEndian: false, out _);
        stream.Write(header);
        WriteBody(stream);
        return stream.ToArray();
    }

    public static ObjRef Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Signature)
            throw new FormatException("The data is not an OBJREF (bad signature).");

        var type = (ObjRefType)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var iid = new Guid(data.Slice(8, 16));
        var body = data[HeaderSize..];
        return type switch
        {
            ObjRefType.Standard => StandardObjRef.ReadBody(iid, body),
            ObjRefType.Handler => HandlerObjRef.ReadBody(iid, body),
            ObjRefType.Custom => CustomObjRef.ReadBody(iid, body),
            _ => throw new FormatException($"Unsupported OBJREF type 0x{(uint)type:X}."),
        };
    }

    protected abstract ObjRefType Type { get; }

    protected abstract void WriteBody(Stream stream);

    protected static StdObjRef ReadStd(ReadOnlySpan<byte> body)
    {
        if (body.Length < StdObjRef.Size)
            throw new FormatException("An OBJREF is too short to hold its STDOBJREF.");

        return StdObjRef.Read(body);
    }

    protected static void WriteStd(Stream stream, StdObjRef std)
    {
        var bytes = new byte[StdObjRef.Size];
        std.Write(bytes);
        stream.Write(bytes);
    }
}
