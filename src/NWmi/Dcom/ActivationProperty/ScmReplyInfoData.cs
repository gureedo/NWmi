using NWmi.Ndr;
using NWmi.Options;
using NWmi.Rpc;

namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// ScmReplyInfoData (MS-DCOM 2.2.22.2.8), wrapping customREMOTE_REPLY_SCM_INFO: where the object exporter of the
/// activated object can be reached.
/// </summary>
/// <param name="Oxid">Identifies the object exporter.</param>
/// <param name="Bindings">String bindings (with endpoints) and security bindings of the exporter.</param>
/// <param name="RemUnknownIpid">IPID of the exporter's IRemUnknown, used to manage references.</param>
/// <param name="AuthnHint">Minimum authentication level the exporter supports; the client may ignore it.</param>
/// <param name="ServerVersion">The exporter's DCOM version.</param>
internal sealed record ScmReplyInfoData(
    ulong Oxid,
    DualStringArray Bindings,
    Guid RemUnknownIpid,
    RpcAuthLevel AuthnHint,
    Version ServerVersion) : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("000001b6-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    /// <summary>The NDR encoding without framing. Servers produce this; it is here for tests and fake servers.</summary>
    public byte[] Encode()
    {
        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteUniquePointer(false, null); // pdwReserved
            w.WriteUniquePointer(true, rw =>
            {
                rw.WriteUInt64(Oxid);
                rw.WriteUniquePointer(true, bw => Bindings.WriteNdr(bw));
                rw.WriteGuid(RemUnknownIpid);
                rw.WriteUInt32((uint)AuthnHint);
                rw.WriteUInt16((ushort)ServerVersion.Major);
                rw.WriteUInt16((ushort)ServerVersion.Minor);
            });
        });

        return writer.ToArray();
    }

    internal static ScmReplyInfoData Decode(ReadOnlySpan<byte> data)
    {
        var reader = new NdrReader(data.ToArray());
        ulong oxid = 0;
        NdrDeferred<DualStringArray>? bindings = null;
        var ipid = Guid.Empty;
        uint hint = 0;
        var version = new Version(0, 0);
        reader.Struct(r =>
        {
            r.ReadUniquePointer(pr => pr.ReadUInt32()); // pdwReserved
            var reply = r.ReadUniquePointer(rr =>
            {
                oxid = rr.ReadUInt64();
                bindings = rr.ReadUniquePointer(DualStringArray.ReadNdr);
                ipid = rr.ReadGuid();
                hint = rr.ReadUInt32();
                version = new Version(rr.ReadUInt16(), rr.ReadUInt16());
                return 0;
            });

            if (reply.IsNull)
                throw new NdrFormatException("ScmReplyInfoData has no reply structure.");

            return 0;
        });

        if (bindings is null || bindings.IsNull)
            throw new NdrFormatException("ScmReplyInfoData has no bindings.");

        return new ScmReplyInfoData(oxid, bindings.Value, ipid, RpcAuthLevels.FromDword(hint), version);
    }
}
