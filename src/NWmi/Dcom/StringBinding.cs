namespace NWmi.Dcom;

/// <summary>A protocol and network address a client can use to reach an object resolver or exporter (MS-DCOM 2.2.19.3).</summary>
/// <param name="TowerId">RPC protocol sequence identifier; <see cref="DualStringArray.TcpTowerId"/> for TCP.</param>
/// <param name="NetworkAddress">"host" or "host[endpoint]", for example "10.0.0.5[49668]".</param>
internal sealed record StringBinding(ushort TowerId, string NetworkAddress)
{
    /// <summary>The host part of <see cref="NetworkAddress"/>.</summary>
    public string Host
    {
        get
        {
            var bracket = NetworkAddress.IndexOf('[', StringComparison.Ordinal);
            return bracket < 0 ? NetworkAddress : NetworkAddress[..bracket];
        }
    }

    /// <summary>The endpoint inside the brackets, or null when the address has none.</summary>
    public string? Endpoint
    {
        get
        {
            var open = NetworkAddress.IndexOf('[', StringComparison.Ordinal);
            var close = NetworkAddress.LastIndexOf(']');
            return open >= 0 && close > open ? NetworkAddress[(open + 1)..close] : null;
        }
    }
}
