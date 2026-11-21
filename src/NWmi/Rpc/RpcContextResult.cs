namespace NWmi.Rpc;

/// <summary>Result of one presentation context in a bind_ack (C706 12.6.3.1, MS-RPCE 2.2.2.4).</summary>
internal enum RpcContextResult : ushort
{
    Acceptance = 0,
    UserRejection = 1,
    ProviderRejection = 2,

    /// <summary>The server's answer to the bind time feature negotiation element.</summary>
    NegotiateAck = 3,
}
