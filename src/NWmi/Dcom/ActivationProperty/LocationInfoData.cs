using NWmi.Ndr;

namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// LocationInfoData (MS-DCOM 2.2.22.2.6). Filled with nulls. Ignored by server.
/// </summary>
internal sealed class LocationInfoData : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("000001a4-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    public byte[] Encode()
    {
        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteUniquePointer(false, null); // machineName
            w.WriteUInt32(0); // processId
            w.WriteUInt32(0); // apartmentId
            w.WriteUInt32(0); // contextId
        });

        return writer.ToArray();
    }
}
