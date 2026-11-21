namespace NWmi.Rpc;

/// <summary>Connection-oriented PDU types (C706 12.6; names as in MS-RPCE).</summary>
internal enum RpcPduType : byte
{
    Request = 0,
    Ping = 1,
    Response = 2,
    Fault = 3,
    Working = 4,
    NoCall = 5,
    Reject = 6,
    Ack = 7,
    ClCancel = 8,
    Fack = 9,
    CancelAck = 10,
    Bind = 11,
    BindAck = 12,
    BindNak = 13,
    AlterContext = 14,
    AlterContextResponse = 15,
    Auth3 = 16,
    Shutdown = 17,
    CoCancel = 18,
    Orphaned = 19,
}
