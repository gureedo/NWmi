using NWmi.Auth;

namespace NWmi.Rpc;

internal sealed class RpcSecurity
{
    public RpcSecurity(IRpcSecurityProvider provider, RpcAuthLevel level = RpcAuthLevel.PacketPrivacy)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (level is not (RpcAuthLevel.Connect or RpcAuthLevel.PacketIntegrity or RpcAuthLevel.PacketPrivacy))
            throw new ArgumentOutOfRangeException(nameof(level), level,
                "Only Connect, PacketIntegrity and PacketPrivacy are supported.");

        Provider = provider;
        Level = level;
    }

    public IRpcSecurityProvider Provider { get; }

    public RpcAuthLevel Level { get; }

    /// <summary>Identifies the security context in the security trailer of each PDU.</summary>
    public uint ContextId { get; set; }

    private int _claimed;

    /// <summary>
    /// The settings for one connection's bind: this instance the first time, and afterwards a copy with a fresh
    /// provider, since a provider's handshake state belongs to a single connection. Reusing a completed NTLM
    /// provider on an object exporter connection fails.
    /// </summary>
    internal RpcSecurity ClaimForConnection() =>
        Interlocked.Exchange(ref _claimed, 1) == 0
            ? this
            : new RpcSecurity(Provider.CreateForNewConnection(), Level)
                { ContextId = ContextId, _claimed = 1 };

    /// <summary>
    /// Whether to offer RPC header signing (PFC_SUPPORT_HEADER_SIGN), which extends the signature over the PDU header
    /// and security trailer. Always offered when messages are protected, as Windows clients do: a Windows server
    /// rejects a signed or sealed call with RPC_S_SEC_PKG_ERROR otherwise. Used only if the server agrees.
    /// </summary>
    internal bool OffersHeaderSigning => ProtectsMessages;

    internal AuthProtection Protection => Level switch
    {
        RpcAuthLevel.PacketPrivacy => AuthProtection.Privacy,
        RpcAuthLevel.PacketIntegrity => AuthProtection.Integrity,
        _ => AuthProtection.Connect,
    };

    /// <summary>True when request and response PDUs carry a signature.</summary>
    internal bool ProtectsMessages => Level >= RpcAuthLevel.PacketIntegrity;
}
