using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

internal sealed class RpcResponsePdu : RpcPdu
{
    public RpcResponsePdu()
    {
        Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment;
    }

    public override RpcPduType Type => RpcPduType.Response;

    public uint AllocHint { get; set; }

    public ushort ContextId { get; set; }

    public byte CancelCount { get; set; }

    public byte[] Stub { get; set; } = [];

    internal override int StubOffset => HeaderSize + 8;

    private protected override int AuthPaddingOrigin => StubOffset;

    protected override void WriteBody(NdrWriter writer)
    {
        writer.WriteUInt32(AllocHint);
        writer.WriteUInt16(ContextId);
        writer.WriteByte(CancelCount);
        writer.WriteByte(0);
        writer.WriteBytes(Stub);
    }

    internal static RpcResponsePdu ReadBody(NdrReader r)
    {
        var pdu = new RpcResponsePdu
        {
            AllocHint = r.ReadUInt32(),
            ContextId = r.ReadUInt16(),
            CancelCount = r.ReadByte(),
        };

        r.ReadByte();
        pdu.Stub = r.ReadBytes(r.Remaining);
        return pdu;
    }
}
