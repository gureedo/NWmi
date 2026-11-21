using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

/// <summary>How an <see cref="ExporterConnection"/> feeds the OIDs of the interfaces it tracks to an attached <see cref="PingKeeper"/>.</summary>
public class ExporterConnectionPingerTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid IpidA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid IpidB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private sealed class Resource : IAsyncDisposable
    {
        public int Disposed;

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    private static byte[] ComplexPingReply() => Hex.Parse("ab00000000000000 | 0000 0000 | 00000000");

    private static async Task<(ExporterConnection Connection, FakeRpcServer Server)> ConnectExporterAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = new byte[12] }];
        server.Start();
        var connection = await ExporterConnection.FromRpcClientAsync(new RpcClient(clientSide), RemUnknownIpid, null, default);
        return (connection, server);
    }

    private static async Task<(PingKeeper Keeper, FakeRpcServer Server)> ConnectPingerAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = ComplexPingReply() }];
        server.Start();
        var rpc = new RpcClient(clientSide);
        var context = (await rpc.BindAsync([ObjectExporter.Interface]))[0];
        return (new PingKeeper(new ObjectExporter(rpc, context), TimeSpan.FromHours(1)), server);
    }

    private static Task Ping(PingKeeper keeper) => keeper.PingNowAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Tracked_oids_are_added_to_the_pinger()
    {
        var (connection, server) = await ConnectExporterAsync();
        var (keeper, pingServer) = await ConnectPingerAsync();
        await using (connection)
        await using (server)
        await using (pingServer)
        {
            connection.AttachPinger(keeper, null);
            connection.Track(new StdObjRef(0, 1, 7, 100, IpidA));
            connection.Track(new StdObjRef(0, 1, 7, 200, IpidB));
            await Ping(keeper);

            var request = Assert.Single(pingServer.Requests);
            Assert.Equal(ObjectExporter.BuildComplexPingRequest(0, 1, [100, 200], []), request.Stub);
        }
    }

    [Fact]
    public async Task An_oid_stays_pinged_until_the_last_interface_on_it_is_released()
    {
        var (connection, server) = await ConnectExporterAsync();
        var (keeper, pingServer) = await ConnectPingerAsync();
        await using (connection)
        await using (server)
        await using (pingServer)
        {
            connection.AttachPinger(keeper, null);
            var first = connection.Track(new StdObjRef(0, 1, 7, 100, IpidA));
            var second = connection.Track(new StdObjRef(0, 1, 7, 100, IpidB)); // another interface of the same object
            await Ping(keeper);

            await first.DisposeAsync();
            await Ping(keeper); // still held by the second: nothing changed, so a SimplePing
            Assert.Equal((ushort)1, pingServer.Requests[^1].OpNum);

            await second.DisposeAsync();
            await Ping(keeper);
            Assert.Equal(ObjectExporter.BuildComplexPingRequest(0xAB, 2, [], [100]), pingServer.Requests[^1].Stub);
        }
    }

    [Fact]
    public async Task Without_a_pinger_tracking_works_and_there_is_none()
    {
        var (connection, server) = await ConnectExporterAsync();
        await using (connection)
        await using (server)
        {
            Assert.Null(connection.Pinger);
            var reference = connection.Track(new StdObjRef(0, 1, 7, 100, IpidA));
            await reference.DisposeAsync();
            Assert.Single(server.Requests); // the release only
        }
    }

    [Fact]
    public async Task Disposing_the_connection_stops_the_pinger_and_disposes_what_it_owned()
    {
        var (connection, server) = await ConnectExporterAsync();
        var (keeper, pingServer) = await ConnectPingerAsync();
        var resource = new Resource();
        await using (server)
        await using (pingServer)
        {
            connection.AttachPinger(keeper, resource);
            connection.Track(new StdObjRef(0, 1, 7, 100, IpidA));
            await Ping(keeper);

            await connection.DisposeAsync();

            Assert.Equal(1, resource.Disposed);
            Assert.Null(connection.Pinger);
            // The keeper's last act is to drop what it held.
            Assert.Equal(ObjectExporter.BuildComplexPingRequest(0xAB, 2, [], [100]), pingServer.Requests[^1].Stub);
        }
    }
}
