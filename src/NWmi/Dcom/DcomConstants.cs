namespace NWmi.Dcom;

internal static class DcomConstants
{
    /// <summary>The TCP port of the RPC endpoint mapper and object resolver, where DCOM activation starts.</summary>
    public const int EndpointMapperPort = 135;

    /// <summary>The DCOM protocol version (COMVERSION, MS-DCOM 2.2.11) this client announces. 5.7 is what current Windows versions send.</summary>
    public static readonly Version ClientVersion = new(5, 7);
}
