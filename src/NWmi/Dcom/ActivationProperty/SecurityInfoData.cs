using NWmi.Ndr;

namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// SecurityInfoData (MS-DCOM 2.2.22.2.7): MS-DCOM says it "SHOULD NOT be sent and MUST be ignored on
/// receipt," but a Windows client always sends it, and a server rejects requests without the full matching set of
/// properties. Sent with both pointers null, since the spec is explicit that the content is ignored either
/// way.
/// </summary>
internal sealed class SecurityInfoData : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("000001a6-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    public byte[] Encode()
    {
        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteUInt32(0); // dwAuthnFlags
            w.WriteUniquePointer(false, null); // pServerInfo
            w.WriteUniquePointer(false, null); // pdwReserved
        });

        return writer.ToArray();
    }
}
