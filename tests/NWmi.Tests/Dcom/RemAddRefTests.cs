using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class RemAddRefTests
{
    private static readonly Guid Ipid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // ORPCTHAT (8 bytes), conformance 2, two per-entry results (S_OK and E_FAIL), and the method's S_OK.
    private static readonly byte[] TwoResults =
        Hex.Parse("00000000 00000000 | 02000000 | 00000000 | 05400080 | 00000000");

    [Fact]
    public void Per_entry_results_are_read()
    {
        var results = RemUnknown2.ParseAddRefReply(TwoResults, expectedCount: 2);
        Assert.Equal([0, unchecked((int)0x80004005)], results);
    }

    [Fact]
    public void A_wrong_number_of_results_is_rejected()
    {
        Assert.Throws<NdrFormatException>(() => RemUnknown2.ParseAddRefReply(TwoResults, expectedCount: 3));
    }

    [Fact]
    public void A_failure_status_of_the_method_is_raised()
    {
        var failed = (byte[])TwoResults.Clone();
        failed[^4] = 0x07;
        failed[^1] = 0x80;
        var ex = Assert.Throws<DcomException>(() => RemUnknown2.ParseAddRefReply(failed, 2));
        Assert.Equal(unchecked((int)0x80000007u), ex.HResult);
    }

    [Fact]
    public void Trailing_data_and_short_replies_are_rejected()
    {
        Assert.Throws<NdrFormatException>(() => RemUnknown2.ParseAddRefReply([..TwoResults, ..new byte[4]], 2));
        Assert.Throws<NdrFormatException>(() => RemUnknown2.ParseAddRefReply([1], 1));
    }

    [Fact]
    public async Task AddRefAsync_calls_opnum_4_and_returns_the_per_entry_results()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = TwoResults }];
        server.Start();
        await using var client = new RpcClient(clientSide);
        var context = (await client.BindAsync([DcomIds.RemUnknown2], cancellationToken: TestContext.Current.CancellationToken))[0];

        var results = await new RemUnknown2(client, context, RemUnknownIpid)
            .AddRefAsync([new RemInterfaceRef(Ipid, 1, 0), new RemInterfaceRef(RemUnknownIpid, 1, 0)], TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)4, request.OpNum);
        Assert.Equal(RemUnknownIpid, request.ObjectUuid);
        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0]);
    }
}
