using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

/// <summary>A request PDU. <see cref="Stub"/> is this fragment's share of the NDR-encoded arguments.</summary>
internal sealed class RpcRequestPdu : RpcPdu
{
    public RpcRequestPdu()
    {
        Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment;
    }

    public override RpcPduType Type => RpcPduType.Request;

    public uint AllocHint { get; set; }

    public ushort ContextId { get; set; }

    public ushort OpNum { get; set; }

    /// <summary>Object UUID, carried when set; DCOM puts the IPID of the target interface here.</summary>
    public Guid? ObjectUuid { get; set; }

    public byte[] Stub { get; set; } = [];

    protected override RpcPfcFlags WireFlags =>
        ObjectUuid is null ? Flags & ~RpcPfcFlags.ObjectUuid : Flags | RpcPfcFlags.ObjectUuid;

    internal override int StubOffset => HeaderSize + 8 + (ObjectUuid is null ? 0 : 16);

    private protected override int AuthPaddingOrigin => StubOffset;

    protected override void WriteBody(NdrWriter writer)
    {
        writer.WriteUInt32(AllocHint);
        writer.WriteUInt16(ContextId);
        writer.WriteUInt16(OpNum);
        if (ObjectUuid is { } uuid)
            writer.WriteGuid(uuid);

        writer.WriteBytes(Stub);
    }

    internal static RpcRequestPdu ReadBody(NdrReader r, RpcPfcFlags flags)
    {
        var pdu = new RpcRequestPdu
        {
            AllocHint = r.ReadUInt32(),
            ContextId = r.ReadUInt16(),
            OpNum = r.ReadUInt16(),
        };

        if ((flags & RpcPfcFlags.ObjectUuid) != 0)
            pdu.ObjectUuid = r.ReadGuid();

        pdu.Stub = r.ReadBytes(r.Remaining);
        return pdu;
    }
}
