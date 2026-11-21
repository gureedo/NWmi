using NWmi.Ndr;

namespace NWmi.Rpc.Pdu;

/// <summary>A bind or alter_context PDU (they share a layout).</summary>
internal sealed class RpcBindPdu : RpcPdu
{
    public RpcBindPdu()
    {
        Flags = RpcPfcFlags.FirstFragment | RpcPfcFlags.LastFragment;
    }

    /// <summary>Either <see cref="RpcPduType.Bind"/> (default) or <see cref="RpcPduType.AlterContext"/>.</summary>
    public RpcPduType PduType { get; init; } = RpcPduType.Bind;

    public override RpcPduType Type => PduType;

    public ushort MaxXmitFrag { get; set; }

    public ushort MaxRecvFrag { get; set; }

    public uint AssocGroupId { get; set; }

    public IReadOnlyList<RpcContextElement> Contexts { get; set; } = [];

    protected override void WriteBody(NdrWriter writer)
    {
        if (Contexts.Count is 0 or > byte.MaxValue)
            throw new RpcProtocolException($"A bind needs between 1 and 255 contexts, not {Contexts.Count}.");

        writer.WriteUInt16(MaxXmitFrag);
        writer.WriteUInt16(MaxRecvFrag);
        writer.WriteUInt32(AssocGroupId);
        writer.WriteByte((byte)Contexts.Count);
        writer.WriteByte(0);
        writer.WriteUInt16(0);
        foreach (var context in Contexts)
        {
            if (context.TransferSyntaxes.Count is 0 or > byte.MaxValue)
                throw new RpcProtocolException("A context needs between 1 and 255 transfer syntaxes.");

            writer.WriteUInt16(context.ContextId);
            writer.WriteByte((byte)context.TransferSyntaxes.Count);
            writer.WriteByte(0);
            writer.Write(context.AbstractSyntax);
            foreach (var transfer in context.TransferSyntaxes)
                writer.Write(transfer);
        }
    }

    internal static RpcBindPdu ReadBody(NdrReader r, RpcPduType type)
    {
        var pdu = new RpcBindPdu
        {
            PduType = type,
            MaxXmitFrag = r.ReadUInt16(),
            MaxRecvFrag = r.ReadUInt16(),
            AssocGroupId = r.ReadUInt32(),
        };

        var count = r.ReadByte();
        r.ReadByte();
        r.ReadUInt16();
        var contexts = new List<RpcContextElement>(count);
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadUInt16();
            var transferCount = r.ReadByte();
            r.ReadByte();
            var abstractSyntax = r.ReadSyntaxId();
            r.EnsureCount(transferCount, 20);
            var transfers = new List<RpcSyntaxId>(transferCount);
            for (var t = 0; t < transferCount; t++)
                transfers.Add(r.ReadSyntaxId());

            contexts.Add(new RpcContextElement(id, abstractSyntax, transfers));
        }

        pdu.Contexts = contexts;
        return pdu;
    }
}
