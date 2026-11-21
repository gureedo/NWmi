namespace NWmi.Rpc;

/// <summary>RPC_C_AUTHN_LEVEL_* values (MS-RPCE 2.2.1.1.8).</summary>
public enum RpcAuthLevel : byte
{
    /// <summary>No authentication (RPC_C_AUTHN_LEVEL_NONE).</summary>
    None = 1,
    /// <summary>Authenticate when the connection is made (RPC_C_AUTHN_LEVEL_CONNECT).</summary>
    Connect = 2,
    /// <summary>Authenticate at the start of each call (RPC_C_AUTHN_LEVEL_CALL).</summary>
    Call = 3,
    /// <summary>Authenticate every packet (RPC_C_AUTHN_LEVEL_PKT).</summary>
    Packet = 4,
    /// <summary>Sign every packet (RPC_C_AUTHN_LEVEL_PKT_INTEGRITY).</summary>
    PacketIntegrity = 5,
    /// <summary>Sign and encrypt every packet (RPC_C_AUTHN_LEVEL_PKT_PRIVACY).</summary>
    PacketPrivacy = 6,
}

