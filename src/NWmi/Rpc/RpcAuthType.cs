namespace NWmi.Rpc;

/// <summary>RPC_C_AUTHN_* values (MS-RPCE 2.2.1.1.7) for the mechanisms this library implements.</summary>
internal enum RpcAuthType : ushort
{
    GssNegotiate = 9,
    WinNt = 10,
    GssKerberos = 16,
}
