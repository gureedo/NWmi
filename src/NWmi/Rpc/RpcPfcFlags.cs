namespace NWmi.Rpc;

/// <summary><c>pfc_flags</c> of the common PDU header.</summary>
[Flags]
internal enum RpcPfcFlags : byte
{
    None = 0,
    FirstFragment = 0x01,
    LastFragment = 0x02,

    /// <summary>PFC_PENDING_CANCEL in most PDUs; PFC_SUPPORT_HEADER_SIGN in bind and alter_context (MS-RPCE 2.2.2.3).</summary>
    PendingCancelOrSupportHeaderSign = 0x04,
    ConcurrentMultiplex = 0x10,
    DidNotExecute = 0x20,
    Maybe = 0x40,
    ObjectUuid = 0x80,
}
