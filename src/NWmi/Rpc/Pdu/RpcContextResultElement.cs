namespace NWmi.Rpc.Pdu;

/// <summary>The server's verdict on one offered context (<c>p_result_t</c>).</summary>
internal sealed record RpcContextResultElement(RpcContextResult Result, ushort Reason, RpcSyntaxId TransferSyntax);
