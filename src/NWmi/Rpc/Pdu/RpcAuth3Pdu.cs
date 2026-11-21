using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

/// <summary>The client's final authentication leg after a bind_ack (C706 12.6.4.7); it carries only a sec_trailer.</summary>
internal sealed class RpcAuth3Pdu : RpcPdu
{
    public RpcAuth3Pdu()
    {
        Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment;
    }

    public override RpcPduType Type => RpcPduType.Auth3;

    protected override void WriteBody(NdrWriter writer) => writer.WriteUInt32(0);
}
