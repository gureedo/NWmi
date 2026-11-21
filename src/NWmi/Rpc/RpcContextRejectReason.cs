namespace NWmi.Rpc;

/// <summary>Reason accompanying a provider rejection of a presentation context.</summary>
internal enum RpcContextRejectReason : ushort
{
    ReasonNotSpecified = 0,
    AbstractSyntaxNotSupported = 1,
    ProposedTransferSyntaxesNotSupported = 2,
    LocalLimitExceeded = 3,
}
