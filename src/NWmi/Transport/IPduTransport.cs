namespace NWmi.Transport;

internal interface IPduTransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default);

    ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default);
}
