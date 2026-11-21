using System.Net.Sockets;
using NWmi.Rpc;

namespace NWmi.Transport;

/// <summary>
/// Frames connection-oriented PDUs on a byte stream: read the 16-byte header, take <c>frag_length</c> from it and read the rest.
/// Used for TCP (ncacn_ip_tcp, MS-RPCE 2.1.1.1) and would serve named pipes as well.
/// </summary>
internal sealed class StreamPduTransport : IPduTransport
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public StreamPduTransport(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(pdu, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var header = new byte[RpcPdu.HeaderSize];
        await _stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        var length = RpcPdu.ReadFragmentLength(header);
        var pdu = new byte[length];
        header.CopyTo(pdu, 0);
        await _stream.ReadExactlyAsync(pdu.AsMemory(RpcPdu.HeaderSize), cancellationToken).ConfigureAwait(false);
        return pdu;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    public static async ValueTask<IPduTransport> ConnectTcpAsync(string host, int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new StreamPduTransport(client.GetStream());
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
