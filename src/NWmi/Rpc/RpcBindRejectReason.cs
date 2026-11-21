namespace NWmi.Rpc;

/// <summary>Reasons in a bind_nak (C706 12.6.3.1, MS-RPCE 2.2.2.5).</summary>
internal enum RpcBindRejectReason : ushort
{
    ReasonNotSpecified = 0,
    TemporaryCongestion = 1,
    LocalLimitExceeded = 2,
    ProtocolVersionNotSupported = 4,
    AuthenticationTypeNotRecognized = 8,
    InvalidChecksum = 9,
}
