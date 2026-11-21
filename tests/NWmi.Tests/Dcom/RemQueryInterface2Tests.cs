using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class RemQueryInterface2Tests
{
    private static readonly Guid Cid = Guid.Parse("12345678-9abc-def0-1122-334455667788");
    private static readonly Guid TargetIpid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Iid = Guid.Parse("00000143-0000-0000-c000-000000000046"); // IUnknown2

    [Fact]
    public void The_request_has_the_documented_layout()
    {
        var request = RemUnknown2.BuildQueryInterfaceRequest(Cid, TargetIpid, [Iid]);

        Assert.Equal(
            Hex.Parse(
                "0500 0700 | 00000000 | 00000000 | 78563412 bc9a f0de 1122334455667788 | 00000000 | " + // ORPCTHIS
                "aaaaaaaa bbbb cccc ddddeeeeeeeeeeee | " + // ripid
                "0100 0000 | 01000000 | " + // cIids, padding, conformance
                "43010000 0000 0000 c000 000000000046"), // the IID
            request);
    }

    [Fact]
    public void The_number_of_iids_is_limited()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RemUnknown2.BuildQueryInterfaceRequest(Cid, TargetIpid, []));
    }

    private static byte[] BuildReply(int[] hresults, byte[]?[] objRefs, uint status = 0)
    {
        var writer = new NdrWriter();
        writer.WriteUInt32(0); // ORPCTHAT flags
        writer.WriteUniquePointer(false, null); // ORPCTHAT extensions
        writer.WriteMaxCount(hresults.Length);
        foreach (var h in hresults)
            writer.WriteInt32(h);

        writer.WriteMaxCount(objRefs.Length);
        foreach (var objRef in objRefs)
            writer.WriteMInterfacePointer(objRef);

        writer.WriteUInt32(status);
        return writer.ToArray();
    }

    [Fact]
    public void A_mix_of_success_and_failure_is_parsed()
    {
        var reply = BuildReply([0, unchecked((int)0x80004002)], [[1, 2, 3], null]);

        var results = RemUnknown2.ParseQueryInterfaceReply(reply, [Iid, DcomIds.IUnknown]);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Succeeded);
        Assert.Equal(new byte[] { 1, 2, 3 }, results[0].ObjRef);
        Assert.False(results[1].Succeeded);
        Assert.Null(results[1].ObjRef);
    }

    [Fact]
    public void A_method_failure_status_is_raised_first()
    {
        var reply = BuildReply([0], [[1]], status: 0x80070005);
        var ex = Assert.Throws<DcomException>(() => RemUnknown2.ParseQueryInterfaceReply(reply, [Iid]));
        Assert.Equal(unchecked((int)0x80070005u), ex.HResult);
    }

    [Fact]
    public void A_success_without_a_pointer_is_rejected()
    {
        var reply = BuildReply([0], [null]);
        Assert.Throws<NdrFormatException>(() => RemUnknown2.ParseQueryInterfaceReply(reply, [Iid]));
    }

    [Fact]
    public void A_count_mismatch_is_rejected()
    {
        var reply = BuildReply([0, 0], [[1], [2]]);
        Assert.Throws<NdrFormatException>(() => RemUnknown2.ParseQueryInterfaceReply(reply, [Iid]));
    }

    [Fact]
    public async Task QueryInterfaceAsync_calls_opnum_6_with_the_remunknown_ipid_as_object_uuid()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
        [
            new RpcResponsePdu
            {
                CallId = fragments[0].CallId,
                ContextId = fragments[0].ContextId,
                Stub = BuildReply([0], [[9, 8, 7]]),
            },
        ];
        server.Start();
        await using var client = new RpcClient(clientSide);
        var context = (await client.BindAsync([DcomIds.RemUnknown2], cancellationToken: TestContext.Current.CancellationToken))[0];

        var results = await new RemUnknown2(client, context, RemUnknownIpid).QueryInterfaceAsync(TargetIpid, [Iid], TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)6, request.OpNum);
        Assert.Equal(RemUnknownIpid, request.ObjectUuid);
        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.Single(results).ObjRef);
    }
}
