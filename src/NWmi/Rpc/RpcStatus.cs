namespace NWmi.Rpc;

/// <summary>Names and Win32 mappings for the status codes that appear in rpc_fault PDUs.</summary>
internal static class RpcStatus
{
    // DCE status codes (C706 appendix N). Only the ones MS-RPCE 3.1.1.5.5 maps to a Win32 error that
    // MS-ERREF documents are listed; any other code is reported as is.
    public const uint NcaOpRangeError = 0x1C010002;
    public const uint NcaUnknownInterface = 0x1C010003;
    public const uint NcaProtocolError = 0x1C01000B;

    // Win32 values from MS-ERREF.
    public const uint AccessDenied = 0x00000005;
    public const uint RpcServerUnavailable = 0x000006BA;
    public const uint RpcServerTooBusy = 0x000006BB;
    public const uint RpcCallFailed = 0x000006BE;
    public const uint RpcProtocolError = 0x000006C0;
    public const uint RpcUnsupportedType = 0x000006C4;
    public const uint RpcUnknownInterface = 0x000006B5;
    public const uint RpcProcnumOutOfRange = 0x000006D1;
    public const uint RpcBadStubData = 0x000006F7;
    public const uint RpcCallCancelled = 0x0000071A;
    public const uint RpcUnsupportedAuthnLevel = 0x0000071D;
    public const uint RpcSecPkgError = 0x00000721;

    private static readonly Dictionary<uint, string> Names = new()
    {
        [AccessDenied] = "ERROR_ACCESS_DENIED",
        [RpcUnknownInterface] = "RPC_S_UNKNOWN_IF",
        [RpcServerUnavailable] = "RPC_S_SERVER_UNAVAILABLE",
        [RpcServerTooBusy] = "RPC_S_SERVER_TOO_BUSY",
        [RpcCallFailed] = "RPC_S_CALL_FAILED",
        [RpcProtocolError] = "RPC_S_PROTOCOL_ERROR",
        [RpcUnsupportedType] = "RPC_S_UNSUPPORTED_TYPE",
        [RpcProcnumOutOfRange] = "RPC_S_PROCNUM_OUT_OF_RANGE",
        [RpcBadStubData] = "RPC_X_BAD_STUB_DATA",
        [RpcCallCancelled] = "RPC_S_CALL_CANCELLED",
        [RpcUnsupportedAuthnLevel] = "RPC_S_UNSUPPORTED_AUTHN_LEVEL",
        [RpcSecPkgError] = "RPC_S_SEC_PKG_ERROR",
        [NcaOpRangeError] = "nca_op_rng_error",
        [NcaUnknownInterface] = "nca_unk_if",
        [NcaProtocolError] = "nca_proto_error",
    };

    public static string Describe(uint status) => Names.GetValueOrDefault(status, "unknown status");

    /// <summary>Maps a DCE status to its Win32 equivalent (MS-RPCE 3.1.1.5.5); other codes are returned unchanged.</summary>
    public static uint ToWin32(uint status) => status switch
    {
        NcaOpRangeError => RpcProcnumOutOfRange,
        NcaUnknownInterface => RpcUnknownInterface,
        NcaProtocolError => RpcProtocolError,
        _ => status,
    };
}
