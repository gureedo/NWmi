using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;
using NWmi.Wmi;

namespace NWmi.Tests.Wmi;

public class WmiOrpcConnectionDisposeTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Disposing_one_connection_twice_leaves_its_siblings_on_the_shared_exporter_usable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        // Answers every call with an empty ORPCTHAT and a zero status.
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = new byte[12] }];
        server.Start();

        var exporter = await ExporterConnection.FromRpcClientAsync(new RpcClient(clientSide), RemUnknownIpid, null, ct);
        var first = await WmiOrpcConnection.FromExporterConnectionAsync(exporter, ownsConnection: true,
            new StdObjRef(0, 5, 0x10, 0x1, Guid.NewGuid()), WmiIds.IidWbemServices, null, ct);
        var sibling = await first.BindSiblingAsync(new StdObjRef(0, 5, 0x10, 0x2, Guid.NewGuid()),
            WmiIds.IidWbemServices, ct);

        await first.DisposeAsync();
        await first.DisposeAsync();

        var reply = await sibling.CallAsync(3, _ => { }, ct);
        Assert.Equal(0, WmiOrpcConnection.ReadStatus(reply, "test"));

        await sibling.DisposeAsync();
    }
}
