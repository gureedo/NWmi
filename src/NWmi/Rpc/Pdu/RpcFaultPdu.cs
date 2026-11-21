using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

/// <summary>
/// An rpc_fault. The error always comes from <see cref="Status"/>, never from stub data (MS-RPCE 2.2.2.8).
/// </summary>
internal sealed class RpcFaultPdu : RpcPdu
{
    /// <summary>Bytes of header and fixed fields; MS-RPCE gives the extended error length as alloc_hint minus this.</summary>
    private const int FixedSize = 0x20;

    public RpcFaultPdu()
    {
        Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment;
    }

    public override RpcPduType Type => RpcPduType.Fault;

    public uint AllocHint { get; set; } = FixedSize;

    public ushort ContextId { get; set; }

    public byte CancelCount { get; set; }

    public uint Status { get; set; }

    /// <summary>RPC extended error information (MS-EERR); its presence is flagged in the low bit of a reserved byte.</summary>
    public byte[]? ExtendedErrorInfo { get; set; }

    protected override void WriteBody(NdrWriter writer)
    {
        writer.WriteUInt32(ExtendedErrorInfo is null ? AllocHint : (uint)(FixedSize + ExtendedErrorInfo.Length));
        writer.WriteUInt16(ContextId);
        writer.WriteByte(CancelCount);
        writer.WriteByte(ExtendedErrorInfo is null ? (byte)0 : (byte)1);
        writer.WriteUInt32(Status);
        writer.WriteUInt32(0);
        if (ExtendedErrorInfo is not null)
            writer.WriteBytes(ExtendedErrorInfo);
    }

    internal static RpcFaultPdu ReadBody(NdrReader r)
    {
        var pdu = new RpcFaultPdu
        {
            AllocHint = r.ReadUInt32(),
            ContextId = r.ReadUInt16(),
            CancelCount = r.ReadByte(),
        };

        var reserved = r.ReadByte();
        pdu.Status = r.ReadUInt32();
        if (r.Remaining >= 4)
            r.ReadUInt32();

        // Clients must ignore any stub data, but the extended error blob is kept when flagged.
        if ((reserved & 1) != 0)
            pdu.ExtendedErrorInfo = r.ReadBytes(r.Remaining);

        return pdu;
    }
}
