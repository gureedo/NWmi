namespace NWmi.Rpc.Pdu;

/// <summary>One presentation context offered in a bind or alter_context (<c>p_cont_elem_t</c>).</summary>
internal sealed record RpcContextElement(
    ushort ContextId,
    RpcSyntaxId AbstractSyntax,
    IReadOnlyList<RpcSyntaxId> TransferSyntaxes);
