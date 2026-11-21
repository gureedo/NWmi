namespace NWmi.Rpc;

/// <summary>
/// RPC_C_IMP_LEVEL_* values (rpcdce.h). MS-RPCE 2.2.1.1.9 leaves the numbers to the implementation, since the level never
/// appears in an RPC message; these are Windows's. The ClientImpLevel field of a DCOM activation request is only specified as
/// an implementation-specific value (MS-DCOM 2.2.22.2.4.1), and Windows clients fill it with one of these.
/// </summary>
internal enum RpcImpersonationLevel : uint
{
    /// <summary>Let the security provider choose (RPC_C_IMP_LEVEL_DEFAULT).</summary>
    Default = 0,

    /// <summary>The server cannot learn who the client is (RPC_C_IMP_LEVEL_ANONYMOUS).</summary>
    Anonymous = 1,

    /// <summary>The server can learn the client's identity but not act as it (RPC_C_IMP_LEVEL_IDENTIFY).</summary>
    Identify = 2,

    /// <summary>The server can act as the client on the server's own machine (RPC_C_IMP_LEVEL_IMPERSONATE).</summary>
    Impersonate = 3,

    /// <summary>The server can act as the client on other machines too (RPC_C_IMP_LEVEL_DELEGATE).</summary>
    Delegate = 4,
}
