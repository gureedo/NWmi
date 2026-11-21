using NWmi.Ndr;
using NWmi.Ndr.Ole;

namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// ActivationContextInfoData (MS-DCOM 2.2.22.2.5).
/// Marshaled client Context (2.2.20) to the server.
/// </summary>
internal sealed class ActivationContextInfoData : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("000001a5-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    /// <summary>CTXMSHLFLAGS_BYVAL (MS-DCOM 2.2.20): the only representation valid on the wire.</summary>
    private const uint ContextMarshalByValue = 0x00000002;

    public byte[] Encode()
    {
        var context = EncodeContext();
        // Preserves the activation-properties convention (see CustomObjRef.ReservedIncludesOwnHeader): this
        // marshaler lives in the same ActivationPropertiesBlob as IActivationPropertiesIn, which uses it.
        var contextObjRef =
            new CustomObjRef(DcomIds.IContext, DcomIds.ContextMarshaler, context, reservedIncludesOwnHeader: true)
                .ToBytes();

        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteInt32(0); // clientOK
            w.WriteInt32(0); // bReserved1
            w.WriteUInt32(0); // dwReserved1
            w.WriteUInt32(0); // dwReserved2
            w.WriteMInterfacePointer(contextObjRef); // pIFDClientCtx
            w.WriteMInterfacePointer(null); // pIFDPrototypeCtx
        });

        return writer.ToArray();
    }

    /// <summary>A marshaled Context (MS-DCOM 2.2.20) with no context properties (<c>Count</c> 0).</summary>
    private static byte[] EncodeContext()
    {
        var writer = new NdrWriter();
        writer.WriteUInt16(1); // MajorVersion
        writer.WriteUInt16(1); // MinVersion
        writer.WriteGuid(Guid.NewGuid()); // ContextId
        writer.WriteUInt32(ContextMarshalByValue); // Flags
        writer.WriteUInt32(0); // Reserved
        writer.WriteUInt32(0); // dwNumExtents
        writer.WriteUInt32(0); // cbExtents
        writer.WriteUInt32(0); // MshlFlags
        writer.WriteUInt32(0); // Count
        writer.WriteInt32(1); // Frozen: TRUE
        return writer.ToArray();
    }
}
