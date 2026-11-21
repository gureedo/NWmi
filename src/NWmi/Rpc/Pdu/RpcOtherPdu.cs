using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

/// <summary>Any other PDU type, kept undecoded.</summary>
internal sealed class RpcOtherPdu(RpcPduType type, byte[] body) : RpcPdu
{
    public override RpcPduType Type { get; } = type;

    public byte[] Body { get; } = body;

    protected override void WriteBody(NdrWriter writer) => writer.WriteBytes(Body);
}
