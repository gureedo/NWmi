using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class TrackedInterfaceReferenceTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid IpidA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid IpidB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private static readonly byte[] OkReply = new byte[12];

    private static async Task<(ExporterConnection Connection, FakeRpcServer Server)> ConnectAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = OkReply }];
        server.Start();

        var connection = await ExporterConnection.FromRpcClientAsync(new RpcClient(clientSide), RemUnknownIpid, null, default);
        return (connection, server);
    }

    [Fact]
    public async Task Disposing_a_tracked_reference_releases_it()
    {
        var (connection, server) = await ConnectAsync();
        await using (connection)
        await using (server)
        {
            var reference = connection.Track(new StdObjRef(0, 5, 1, 2, IpidA));
            await reference.DisposeAsync();

            var request = Assert.Single(server.Requests);
            Assert.Equal((ushort)5, request.OpNum); // RemRelease
            Assert.Equal(RemUnknownIpid, request.ObjectUuid);
        }
    }

    [Fact]
    public async Task Disposing_twice_releases_only_once()
    {
        var (connection, server) = await ConnectAsync();
        await using (connection)
        await using (server)
        {
            var reference = connection.Track(new StdObjRef(0, 5, 1, 2, IpidA));
            await reference.DisposeAsync();
            await reference.DisposeAsync();

            Assert.Single(server.Requests);
        }
    }

    [Fact]
    public async Task Disposing_the_connection_releases_every_still_tracked_reference()
    {
        var (connection, server) = await ConnectAsync();
        await using (server)
        {
            var a = connection.Track(new StdObjRef(0, 1, 1, 10, IpidA));
            var b = connection.Track(new StdObjRef(0, 2, 1, 11, IpidB));
            await a.DisposeAsync(); // released explicitly now

            await connection.DisposeAsync(); // must release b, but not release a again

            Assert.Equal(2, server.Requests.Count);
            Assert.All(server.Requests, r => Assert.Equal((ushort)5, r.OpNum)); // both are RemRelease calls
        }
    }

    [Fact]
    public async Task Tracking_after_the_connection_is_disposed_is_rejected()
    {
        var (connection, server) = await ConnectAsync();
        await server.DisposeAsync();
        await connection.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => connection.Track(new StdObjRef(0, 1, 1, 1, IpidA)));
    }

    [Fact]
    public async Task A_release_failure_during_dispose_is_swallowed()
    {
        var (connection, server) = await ConnectAsync();
        var reference = connection.Track(new StdObjRef(0, 1, 1, 1, IpidA));
        await server.DisposeAsync(); // the connection is now unusable from the client's side

        await reference.DisposeAsync(); // must not throw
        await connection.DisposeAsync(); // nor must this
    }
}
