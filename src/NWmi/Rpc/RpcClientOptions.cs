namespace NWmi.Rpc;

internal sealed class RpcClientOptions
{
    /// <summary>Largest fragment we offer to send (<c>max_xmit_frag</c>). Windows uses 5840 over TCP.</summary>
    public ushort MaxXmitFrag { get; set; } = 5840;

    /// <summary>Largest fragment we offer to receive (<c>max_recv_frag</c>).</summary>
    public ushort MaxRecvFrag { get; set; } = 5840;

    /// <summary>Limit on the stub data of one reassembled response.</summary>
    public int MaxResponseSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>How long one bind, alter_context or call may take. <see cref="Timeout.InfiniteTimeSpan"/> disables it.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Add the bind time feature negotiation element to the first bind (MS-RPCE 3.3.1.5.3). The bitmask
    /// advertises security context multiplexing and keep-connection-on-orphan, which this client does not use,
    /// so it is off by default and the bitmask is zero.
    /// </summary>
    public bool OfferBindTimeFeatureNegotiation { get; set; }

    internal void Validate()
    {
        const int minFragment = 128;
        if (MaxXmitFrag < minFragment || MaxRecvFrag < minFragment)
            throw new ArgumentOutOfRangeException(nameof(MaxXmitFrag),
                $"Fragment sizes must be at least {minFragment} bytes.");

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxResponseSize);
    }
}
