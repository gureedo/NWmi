using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class DcomClientTests
{
    private static readonly Guid ClassId = Guid.Parse("8bc3f05e-d86b-11d0-a075-00c04fb68820");
    private static readonly Guid Login = Guid.Parse("f309ad18-d86a-11d0-a075-00c04fb68820");

    private const string ServerAlive2Reply =
        "0500 0700 | 00000200 | 0e000000 | 0e00 0a00 | " +
        "0700 3100 2e00 3200 2e00 3300 2e00 3400 0000 0000 | 0a00 ffff 0000 0000 | 00000000 | 00000000";

    private static async Task<(DcomClient Client, FakeRpcServer Server)> ConnectAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
        {
            var stub = fragments[0].OpNum == 5
                ? Hex.Parse(ServerAlive2Reply)
                : RemoteCreateInstanceReplyTests.Reply(DcomIds.ActivationPropertiesOut);
            return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = stub }];
        };
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var client = await DcomClient.FromRpcClientAsync(rpcClient, security: null, cancellationToken: default);
        return (client, server);
    }

    [Fact]
    public async Task Each_interface_is_bound_in_its_own_call()
    {
        // A real Windows endpoint mapper rejects whichever interface is listed second in a single combined
        // bind (see the remarks on DcomClient.FromRpcClientAsync), so the
        // two interfaces must go through a bind followed by a separate alter_context, not one bind with two contexts.
        var (client, server) = await ConnectAsync();
        await using (client)
        await using (server)
        {
            Assert.Equal(2, server.Binds.Count);
            Assert.Equal(RpcPduType.Bind, server.Binds[0].Type);
            Assert.Single(server.Binds[0].Contexts);
            Assert.Equal(RpcPduType.AlterContext, server.Binds[1].Type);
            Assert.Single(server.Binds[1].Contexts);
        }
    }

    [Fact]
    public async Task ServerAlive2_and_CreateInstance_go_through_the_bound_contexts()
    {
        var (client, server) = await ConnectAsync();
        await using (client)
        await using (server)
        {
            var alive = await client.Exporter.ServerAlive2Async(TestContext.Current.CancellationToken);
            Assert.Equal("1.2.3.4", Assert.Single(alive.Bindings.StringBindings).NetworkAddress);

            var activated = await client.Activator.CreateInstanceAsync(ClassId, [Login], TestContext.Current.CancellationToken);
            Assert.Equal(Login, activated.GetStandardObjRef(0).Iid);

            Assert.Equal(2, server.Requests.Count);
            Assert.Equal((ushort)5, server.Requests[0].OpNum);
            Assert.Equal((ushort)4, server.Requests[1].OpNum);
            Assert.Equal(server.Requests[0].ContextId, server.Binds[0].Contexts[0].ContextId);
            Assert.Equal(server.Requests[1].ContextId, server.Binds[1].Contexts[0].ContextId);
        }
    }

    [Fact]
    public async Task Disposing_the_client_disposes_the_underlying_connection()
    {
        var (client, server) = await ConnectAsync();
        await server.DisposeAsync();
        await client.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Exporter.ServerAlive2Async(TestContext.Current.CancellationToken));
    }
}
