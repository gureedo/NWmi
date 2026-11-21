using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

/// <summary>A bind_nak: the server refuses the whole bind (MS-RPCE 2.2.2.9).</summary>
internal sealed class RpcBindNakPdu : RpcPdu
{
    public RpcBindNakPdu()
    {
        Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment;
    }

    public override RpcPduType Type => RpcPduType.BindNak;

    public RpcBindRejectReason Reason { get; set; }

    /// <summary>Protocol versions the server supports, as (major, minor) pairs.</summary>
    public IReadOnlyList<(byte Major, byte Minor)> SupportedVersions { get; set; } = [];

    protected override void WriteBody(NdrWriter writer)
    {
        writer.WriteUInt16((ushort)Reason);
        writer.WriteByte((byte)SupportedVersions.Count);
        foreach (var (major, minor) in SupportedVersions)
        {
            writer.WriteByte(major);
            writer.WriteByte(minor);
        }
    }

    internal static RpcBindNakPdu ReadBody(NdrReader r)
    {
        var pdu = new RpcBindNakPdu { Reason = (RpcBindRejectReason)r.ReadUInt16() };
        if (r.Remaining > 0)
        {
            var count = r.ReadByte();
            r.EnsureCount(count, 2);
            var versions = new List<(byte, byte)>(count);
            for (var i = 0; i < count; i++)
                versions.Add((r.ReadByte(), r.ReadByte()));

            pdu.SupportedVersions = versions;
        }

        return pdu;
    }
}
