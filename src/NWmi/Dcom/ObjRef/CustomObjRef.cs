using System.Buffers.Binary;

namespace NWmi.Dcom;

/// <summary>
/// OBJREF_CUSTOM: the object marshaled itself into opaque data for a custom marshaler.
/// WMI uses it for IWbemClassObject, whose data is a WMIO object (MS-WMI 2.2.4).
/// </summary>
internal sealed class CustomObjRef : ObjRef
{
    private const int FixedSize = 24; // clsid, cbExtension, reserved

    public CustomObjRef(Guid iid, Guid clsid, byte[] objectData, bool reservedIncludesOwnHeader = false)
        : base(iid)
    {
        ArgumentNullException.ThrowIfNull(objectData);
        Clsid = clsid;
        ObjectData = objectData;
        ReservedIncludesOwnHeader = reservedIncludesOwnHeader;
    }

    /// <summary>Identifies the custom marshaler that understands <see cref="ObjectData"/>.</summary>
    public Guid Clsid { get; }

    public byte[] ObjectData { get; }

    /// <summary>
    /// MS-DCOM calls OBJREF_CUSTOM's "reserved" field unused, but it is really opaque to the specific custom
    /// marshaler (<see cref="Clsid"/>), which is free to give it its own meaning. Two different marshalers differ:
    /// the CIM-object one (<c>WmiClassObjectCodec</c>/<c>WmiContext</c>, used for
    /// <c>ExecMethod</c>'s and <c>PutInstance</c>'s parameters) writes <see cref="ObjectData"/>'s exact byte
    /// count, no more — <see langword="false"/>, the default; the activation-properties one
    /// (<see cref="RemoteScmActivator"/>'s <c>IActivationPropertiesIn</c>) instead writes the count plus 8, and its
    /// own unmarshaler fails with STG_E_READFAULT if it is not: <see langword="true"/>. Using the CIM-object
    /// convention for a <c>PutInstance</c> call handed the server 8 bytes more than the real instance data,
    /// which <c>PutInstance</c>'s stricter deserializer (unlike <c>ExecMethod</c>'s) rejected with
    /// WBEM_E_INVALID_CLASS.
    /// </summary>
    public bool ReservedIncludesOwnHeader { get; }

    protected override ObjRefType Type => ObjRefType.Custom;

    protected override void WriteBody(Stream stream)
    {
        stream.Write(Clsid.ToByteArray());
        Span<byte> fields = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(fields, 0); // cbExtension
        var reserved = (uint)ObjectData.Length + (ReservedIncludesOwnHeader ? 8u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[4..], reserved);
        stream.Write(fields);
        stream.Write(ObjectData);
    }

    internal static CustomObjRef ReadBody(Guid iid, ReadOnlySpan<byte> body)
    {
        if (body.Length < FixedSize)
            throw new FormatException("An OBJREF_CUSTOM is too short.");

        return new CustomObjRef(iid, new Guid(body[..16]), [.. body[FixedSize..]]);
    }
}
