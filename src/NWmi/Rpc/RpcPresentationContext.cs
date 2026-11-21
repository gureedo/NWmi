namespace NWmi.Rpc;

internal sealed class RpcPresentationContext
{
    internal RpcPresentationContext(ushort contextId, RpcSyntaxId interfaceId, RpcSyntaxId transferSyntax)
    {
        ContextId = contextId;
        Interface = interfaceId;
        TransferSyntax = transferSyntax;
    }

    public ushort ContextId { get; }

    public RpcSyntaxId Interface { get; }

    public RpcSyntaxId TransferSyntax { get; }

    public override string ToString() => $"context {ContextId}: {Interface}";
}
