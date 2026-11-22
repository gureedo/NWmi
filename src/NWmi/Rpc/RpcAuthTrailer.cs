using NWmi.Options;

namespace NWmi.Rpc;

/// <summary>The security trailer (<c>sec_trailer</c>) and token that end an authenticated PDU (MS-RPCE 2.2.2.11).</summary>
/// <param name="AuthType">RPC_C_AUTHN_* value.</param>
/// <param name="AuthLevel">RPC_C_AUTHN_LEVEL_* value.</param>
/// <param name="ContextId">Identifies the security context on the connection.</param>
/// <param name="Value">The security provider's token or signature.</param>
internal sealed record RpcAuthTrailer(RpcAuthType AuthType, RpcAuthLevel AuthLevel, uint ContextId, byte[] Value)
{
    /// <summary>Padding bytes between the body and the trailer. Set when parsing; computed when serializing.</summary>
    public byte PadLength { get; init; }
}
