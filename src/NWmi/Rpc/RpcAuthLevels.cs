namespace NWmi.Rpc;

internal static class RpcAuthLevels
{
    /// <summary>
    /// An authentication level read from a DWORD on the wire. A value outside the enum's byte range saturates to 255,
    /// which is not a defined level.
    /// </summary>
    public static RpcAuthLevel FromDword(uint value) => (RpcAuthLevel)(byte)Math.Min(value, byte.MaxValue);
}
