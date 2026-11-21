using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class PingTests
{

    [Fact]
    public void ComplexPing_request_with_both_lists_has_the_documented_layout()
    {
        var request = ObjectExporter.BuildComplexPingRequest(0x1122334455667788, 7, [0x11], [0x22, 0x33]);

        Assert.Equal(
            Hex.Parse(
                "8877665544332211 | 0700 0100 0200 | 0000 | " + // pSetId, SequenceNum, cAddToSet, cDelFromSet, padding
                "00000200 | 01000000 | 1100000000000000 | " + // AddToSet: referent, max_count, one OID
                "04000200 | 02000000 | 2200000000000000 3300000000000000"), // DelFromSet: referent, max_count, two OIDs
            request);
    }

    [Fact]
    public void ComplexPing_request_with_empty_lists_sends_null_pointers()
    {
        var request = ObjectExporter.BuildComplexPingRequest(0, 1, [], []);

        Assert.Equal(Hex.Parse("0000000000000000 | 0100 0000 0000 | 0000 | 00000000 | 00000000"), request);
    }

    [Fact]
    public void ComplexPing_reply_is_decoded()
    {
        var reply = Hex.Parse("9988776655443322 | 0500 0000 | 00000000"); // SETID, backoff + padding, status
        var result = ObjectExporter.ParseComplexPingReply(reply);

        Assert.Equal(0x2233445566778899UL, result.SetId);
        Assert.Equal((ushort)5, result.BackoffFactor);
    }

    [Fact]
    public void ComplexPing_failure_status_is_raised()
    {
        var reply = Hex.Parse("0000000000000000 | 0000 0000 | 7a000780"); // OR_INVALID_SET-like failure
        var ex = Assert.Throws<DcomException>(() => ObjectExporter.ParseComplexPingReply(reply));
        Assert.Equal(unchecked((int)0x8007007au), ex.HResult);
    }

    [Fact]
    public async Task SimplePing_sends_opnum_1_with_the_set_id()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = new byte[4] }];
        server.Start();
        await using var client = new RpcClient(clientSide);
        var context = (await client.BindAsync([ObjectExporter.Interface], cancellationToken: TestContext.Current.CancellationToken))[0];

        await new ObjectExporter(client, context).SimplePingAsync(0x1122334455667788, TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)1, request.OpNum);
        Assert.Equal(Hex.Parse("8877665544332211"), request.Stub);
    }

    [Fact]
    public async Task ComplexPing_sends_opnum_2_and_returns_the_new_set_id()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
        [
            new RpcResponsePdu
            {
                CallId = fragments[0].CallId,
                ContextId = fragments[0].ContextId,
                Stub = Hex.Parse("0102030405060708 | 0000 0000 | 00000000"),
            },
        ];
        server.Start();
        await using var client = new RpcClient(clientSide);
        var context = (await client.BindAsync([ObjectExporter.Interface], cancellationToken: TestContext.Current.CancellationToken))[0];

        var result = await new ObjectExporter(client, context).ComplexPingAsync(0, 1, [0x99], [], TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)2, request.OpNum);
        Assert.Equal(0x0807060504030201UL, result.SetId);
    }
}
