using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class ExporterConnectionTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Ipid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static readonly byte[] OkReply = new byte[12];

    private static async Task<(ExporterConnection Connection, FakeRpcServer Server)> ConnectAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = OkReply }];
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var connection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: default);
        return (connection, server);
    }

    [Fact]
    public async Task IRemUnknown2_is_bound_and_calls_use_the_given_ipid_as_object_uuid()
    {
        var (connection, server) = await ConnectAsync();
        await using (connection)
        await using (server)
        {
            await connection.RemUnknown.ReleaseAsync([new RemInterfaceRef(Ipid, 5, 0)], TestContext.Current.CancellationToken);

            Assert.Equal(DcomIds.RemUnknown2.Uuid, Assert.Single(server.Binds).Contexts[0].AbstractSyntax.Uuid);
            Assert.Equal(RemUnknownIpid, Assert.Single(server.Requests).ObjectUuid);
        }
    }

    [Fact]
    public void SelectExporterTarget_is_used_to_pick_the_host_and_port()
    {
        var bindings = new DualStringArray([new StringBinding(DualStringArray.TcpTowerId, "10.0.0.5[49668]")], []);
        var target = BindingSelector.SelectExporterTarget(bindings);
        Assert.Equal("10.0.0.5", target.Host);
        Assert.Equal(49668, target.Port);
    }
}
