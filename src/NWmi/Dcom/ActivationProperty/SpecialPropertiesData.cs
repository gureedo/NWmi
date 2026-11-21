using NWmi.Ndr;

namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// SpecialPropertiesData (MS-DCOM 2.2.22.2.2).
/// Session targeting and other miscellaneous activation parameters.
/// </summary>
internal sealed class SpecialPropertiesData : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("000001b9-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    /// <summary>Any logon session is acceptable to the client.</summary>
    private const uint AnySessionId = 0xFFFFFFFF;

    public byte[] Encode()
    {
        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteUInt32(AnySessionId); // dwSessionId
            w.WriteInt32(0); // fRemoteThisSessionId: FALSE, since dwSessionId is 0xFFFFFFFF
            w.WriteInt32(0); // fClientImpersonating
            w.WriteInt32(0); // fPartitionIDPresent
            w.WriteUInt32(0); // dwDefaultAuthnLvl
            w.WriteGuid(Guid.Empty); // guidPartition
            w.WriteUInt32(0); // dwPRTFlags
            w.WriteUInt32(InstantiationInfoData.ClassContextRemoteServer); // dwOrigClsctx
            w.WriteUInt32(0); // dwFlags
            w.WriteUInt32(0); // Reserved1
            w.WriteUInt64(0); // Reserved2
            for (var i = 0; i < 5; i++)
                w.WriteUInt32(0); // Reserved3[5]
        });

        return writer.ToArray();
    }
}
