using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class RemReleaseTests
{
    private static readonly Guid Cid = Guid.Parse("12345678-9abc-def0-1122-334455667788");
    private static readonly Guid Ipid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly byte[] OkReply = new byte[12]; // ORPCTHAT (8 bytes) and S_OK

    [Fact]
    public void The_request_has_the_documented_layout()
    {
        var request = RemUnknown2.BuildReferenceRequest(Cid, [new RemInterfaceRef(Ipid, 5, 0)]);

        Assert.Equal(
            Hex.Parse(
                "0500 0700 | 00000000 | 00000000 | 78563412 bc9a f0de 1122334455667788 | 00000000 | " + // ORPCTHIS
                "0100 0000 | 01000000 | " + // count, padding, conformance
                "aaaaaaaa bbbb cccc ddddeeeeeeeeeeee | 05000000 | 00000000"), // the entry
            request);
    }

    [Fact]
    public void Several_entries_are_sent_as_one_array()
    {
        var request = RemUnknown2.BuildReferenceRequest(
            Cid, [new RemInterfaceRef(Ipid, 1, 0), new RemInterfaceRef(RemUnknownIpid, 2, 3)]);

        Assert.Equal(32 + 2 + 2 + 4 + 2 * 24, request.Length);
        Assert.Equal(2, BitConverter.ToUInt16(request, 32));
        Assert.Equal(2u, BitConverter.ToUInt32(request, 36));
    }

    [Fact]
    public void The_number_of_entries_is_limited()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RemUnknown2.BuildReferenceRequest(Cid, []));
    }

    [Fact]
    public void A_status_only_reply_is_checked()
    {
        RemUnknown2.ParseStatusOnlyReply(OkReply);

        var failed = (byte[])OkReply.Clone();
        failed[8] = 0x57;
        failed[11] = 0x80;
        var ex = Assert.Throws<DcomException>(() => RemUnknown2.ParseStatusOnlyReply(failed));
        Assert.Equal(unchecked((int)0x80000057u), ex.HResult);
    }

    [Fact]
    public void Extra_or_missing_bytes_are_rejected()
    {
        Assert.Throws<NdrFormatException>(() => RemUnknown2.ParseStatusOnlyReply(new byte[16]));
        Assert.Throws<NdrFormatException>(() => RemUnknown2.ParseStatusOnlyReply([1, 2]));
    }

    [Fact]
    public async Task ReleaseAsync_calls_opnum_5_with_the_remunknown_ipid_as_object_uuid()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = OkReply }];
        server.Start();
        await using var client = new RpcClient(clientSide);
        var context = (await client.BindAsync([DcomIds.RemUnknown2], cancellationToken: TestContext.Current.CancellationToken))[0];

        await new RemUnknown2(client, context, RemUnknownIpid).ReleaseAsync([new RemInterfaceRef(Ipid, 5, 0)], TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)5, request.OpNum);
        Assert.Equal(RemUnknownIpid, request.ObjectUuid);
        Assert.Equal(32 + 2 + 2 + 4 + 24, request.Stub.Length);
    }
}
