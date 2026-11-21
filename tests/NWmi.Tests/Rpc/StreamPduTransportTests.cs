using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Transport;

namespace NWmi.Tests.Rpc;

public class StreamPduTransportTests
{
    /// <summary>Hands out at most one byte per read, like a slow network.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _position >= data.Length || count == 0 ? 0 : Take(buffer.AsSpan(offset, 1));

        public override int Read(Span<byte> buffer) =>
            _position >= data.Length || buffer.Length == 0 ? 0 : Take(buffer[..1]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Take(Span<byte> destination)
        {
            destination[0] = data[_position++];
            return 1;
        }
    }

    private static byte[] Response(uint callId, int stubLength) =>
        new RpcResponsePdu { CallId = callId, Stub = new byte[stubLength] }.Serialize();

    [Fact]
    public async Task Pdus_are_reassembled_from_single_byte_reads_and_split_at_frag_length()
    {
        var first = Response(1, 10);
        var second = Response(2, 0);
        await using var transport = new StreamPduTransport(new TrickleStream([.. first, .. second]));

        Assert.Equal(first, await transport.ReceiveAsync(TestContext.Current.CancellationToken));
        Assert.Equal(second, await transport.ReceiveAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await transport.ReceiveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_connection_closed_in_the_middle_of_a_pdu_is_an_end_of_stream()
    {
        var pdu = Response(1, 40);
        await using var transport = new StreamPduTransport(new MemoryStream(pdu[..30]));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await transport.ReceiveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_frag_length_smaller_than_the_header_is_rejected()
    {
        var bytes = Hex.Parse("05 00 02 03 10000000 | 0800 0000 | 01000000");
        await using var transport = new StreamPduTransport(new MemoryStream(bytes));
        await Assert.ThrowsAsync<RpcProtocolException>(async () => await transport.ReceiveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Big_endian_peers_are_rejected_before_frag_length_is_trusted()
    {
        var bytes = Hex.Parse("05 00 02 03 00000000 | 0000 0020 | 01000000");
        await using var transport = new StreamPduTransport(new MemoryStream(bytes));
        await Assert.ThrowsAsync<RpcProtocolException>(async () => await transport.ReceiveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sent_pdus_reach_the_stream_unchanged()
    {
        var sink = new MemoryStream();
        await using var transport = new StreamPduTransport(sink);
        var pdu = Response(5, 12);
        await transport.SendAsync(pdu, TestContext.Current.CancellationToken);
        Assert.Equal(pdu, sink.ToArray());
    }
}
