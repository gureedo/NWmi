using NWmi.Transport;

namespace NWmi.Tests.Rpc;

/// <summary>Wraps a transport to record every PDU on the wire and, optionally, alter what the client receives.</summary>
internal sealed class TappingTransport(IPduTransport inner) : IPduTransport
{
    public List<byte[]> Sent { get; } = [];

    public List<byte[]> Received { get; } = [];

    /// <summary>Called with each received PDU; return the bytes the client should see.</summary>
    public Func<byte[], byte[]>? RewriteReceived { get; set; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
    {
        Sent.Add(pdu.ToArray());
        return inner.SendAsync(pdu, cancellationToken);
    }

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var pdu = await inner.ReceiveAsync(cancellationToken);
        Received.Add((byte[])pdu.Clone());
        return RewriteReceived?.Invoke(pdu) ?? pdu;
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
