using System.Text;
using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

/// <summary>A bind_ack or alter_context_resp PDU (they share a layout).</summary>
internal sealed class RpcBindAckPdu : RpcPdu
{
    public RpcBindAckPdu()
    {
        Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment;
    }

    /// <summary>Either <see cref="RpcPduType.BindAck"/> (default) or <see cref="RpcPduType.AlterContextResponse"/>.</summary>
    public RpcPduType PduType { get; init; } = RpcPduType.BindAck;

    public override RpcPduType Type => PduType;

    public ushort MaxXmitFrag { get; set; }

    public ushort MaxRecvFrag { get; set; }

    public uint AssocGroupId { get; set; }

    /// <summary>The port or pipe the server listens on (<c>sec_addr</c>), e.g. "135"; empty when not given.</summary>
    public string SecondaryAddress { get; set; } = "";

    public IReadOnlyList<RpcContextResultElement> Results { get; set; } = [];

    protected override void WriteBody(NdrWriter writer)
    {
        writer.WriteUInt16(MaxXmitFrag);
        writer.WriteUInt16(MaxRecvFrag);
        writer.WriteUInt32(AssocGroupId);

        if (SecondaryAddress.Length == 0)
            writer.WriteUInt16(0);
        else
        {
            var address = Encoding.ASCII.GetBytes(SecondaryAddress);
            writer.WriteUInt16((ushort)(address.Length + 1));
            writer.WriteBytes(address);
            writer.WriteByte(0);
        }

        writer.Align(4);
        if (Results.Count > byte.MaxValue)
            throw new RpcProtocolException("A bind_ack holds at most 255 results.");

        writer.WriteByte((byte)Results.Count);
        writer.WriteByte(0);
        writer.WriteUInt16(0);
        foreach (var result in Results)
        {
            writer.WriteUInt16((ushort)result.Result);
            writer.WriteUInt16(result.Reason);
            writer.Write(result.TransferSyntax);
        }
    }

    internal static RpcBindAckPdu ReadBody(NdrReader r, RpcPduType type)
    {
        var pdu = new RpcBindAckPdu
        {
            PduType = type,
            MaxXmitFrag = r.ReadUInt16(),
            MaxRecvFrag = r.ReadUInt16(),
            AssocGroupId = r.ReadUInt32(),
        };

        var addressLength = r.ReadUInt16();
        var address = r.ReadBytes(addressLength);
        pdu.SecondaryAddress = Encoding.ASCII.GetString(address).TrimEnd('\0');
        r.Align(4);

        var count = r.ReadByte();
        r.ReadByte();
        r.ReadUInt16();
        r.EnsureCount(count, 24);
        var results = new List<RpcContextResultElement>(count);
        for (var i = 0; i < count; i++)
        {
            var result = (RpcContextResult)r.ReadUInt16();
            var reason = r.ReadUInt16();
            results.Add(new RpcContextResultElement(result, reason, r.ReadSyntaxId()));
        }

        pdu.Results = results;
        return pdu;
    }
}
