using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class PingKeeperTests
{
    private static async Task<(ObjectExporter Exporter, FakeRpcServer Server, RpcClient RpcClient)> ConnectAsync(
        Func<List<RpcRequestPdu>, IEnumerable<RpcPdu>?>? callHandler = null)
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        if (callHandler is not null)
            server.CallHandler = callHandler;

        server.Start();
        var rpcClient = new RpcClient(clientSide);
        var context = (await rpcClient.BindAsync([ObjectExporter.Interface]))[0];
        return (new ObjectExporter(rpcClient, context), server, rpcClient);
    }

    private static byte[] ComplexPingReply(ulong setId, ushort backoff = 0) =>
        Hex.Parse($"{Reverse(setId)} | {Reverse(backoff, 2)} 0000 | 00000000");

    private static string Reverse(ulong value, int bytes = 8) =>
        Convert.ToHexString(BitConverter.GetBytes(value)[..bytes]).ToLowerInvariant();

    [Fact]
    public async Task First_ping_creates_a_set_with_everything_added_so_far()
    {
        var (exporter, server, rpcClient) = await ConnectAsync(fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = ComplexPingReply(0xAB) }]);
        await using var client = rpcClient;
        await using var server2 = server;

        var keeper = new PingKeeper(exporter, TimeSpan.FromHours(1)); // long period: we drive pings manually
        keeper.Add(1);
        keeper.Add(2);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)2, request.OpNum); // ComplexPing
        var decoded = ObjectExporter.BuildComplexPingRequest(0, 1, [1, 2], []);
        Assert.Equal(decoded, request.Stub);

        await keeper.DisposeAsync();
    }

    [Fact]
    public async Task No_membership_change_sends_a_simple_ping_once_a_set_exists()
    {
        var calls = new List<ushort>();
        var (exporter, server, rpcClient) = await ConnectAsync(fragments =>
        {
            calls.Add(fragments[0].OpNum);
            var stub = fragments[0].OpNum == 2 ? ComplexPingReply(0xAB) : new byte[4];
            return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = stub }];
        });
        await using var client = rpcClient;
        await using var server2 = server;

        var keeper = new PingKeeper(exporter, TimeSpan.FromHours(1));
        keeper.Add(1);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken); // ComplexPing: creates the set
        await keeper.PingNowAsync(TestContext.Current.CancellationToken); // nothing changed: SimplePing

        Assert.Equal([2, 1], calls);
        await keeper.DisposeAsync();
    }

    [Fact]
    public async Task Adding_and_removing_after_the_set_exists_sends_a_complex_ping_again()
    {
        var (exporter, server, rpcClient) = await ConnectAsync(fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = ComplexPingReply(0xAB) }]);
        await using var client = rpcClient;
        await using var server2 = server;

        var keeper = new PingKeeper(exporter, TimeSpan.FromHours(1));
        keeper.Add(1);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);

        keeper.Add(2);
        keeper.Remove(1);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, server.Requests.Count);
        var second = server.Requests[1];
        Assert.Equal((ushort)2, second.OpNum);
        var expected = ObjectExporter.BuildComplexPingRequest(0xAB, 2, [2], [1]);
        Assert.Equal(expected, second.Stub);

        await keeper.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_ping_is_recorded_and_does_not_throw()
    {
        var (exporter, server, rpcClient) = await ConnectAsync(fragments =>
            [new RpcFaultPdu { CallId = fragments[0].CallId, Status = RpcStatus.AccessDenied }]);
        await using var client = rpcClient;
        await using var server2 = server;

        var keeper = new PingKeeper(exporter, TimeSpan.FromHours(1));
        keeper.Add(1);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(keeper.LastFailure);
        await keeper.DisposeAsync();
    }

    [Fact]
    public async Task A_lost_connection_is_replaced_and_the_same_set_goes_on_being_pinged()
    {
        Func<List<RpcRequestPdu>, IEnumerable<RpcPdu>?> answer = fragments =>
        {
            var stub = fragments[0].OpNum == 2 ? ComplexPingReply(0xAB) : new byte[4];
            return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = stub }];
        };
        var (exporter, server, rpcClient) = await ConnectAsync(answer);
        await using var server1 = server;

        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var replacementServer = new FakeRpcServer(serverSide) { CallHandler = answer };
        replacementServer.Start();
        await using var server2 = replacementServer;

        var reconnects = 0;
        var keeper = new PingKeeper(exporter, TimeSpan.FromHours(1), ct =>
        {
            reconnects++;
            return DcomClient.FromRpcClientAsync(new RpcClient(clientSide), null, ct);
        });
        keeper.Add(1);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);
        Assert.Null(keeper.LastFailure);

        await rpcClient.DisposeAsync(); // the connection the pings travel on is gone
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(keeper.LastFailure);
        Assert.Equal(0, reconnects);

        await keeper.PingNowAsync(TestContext.Current.CancellationToken);

        Assert.Null(keeper.LastFailure);
        Assert.Equal(1, reconnects);
        Assert.Equal((ushort)1, Assert.Single(replacementServer.Requests).OpNum); // SimplePing on the existing set
        await keeper.DisposeAsync();
    }

    [Fact]
    public async Task A_fault_from_the_server_does_not_replace_the_connection()
    {
        var (exporter, server, rpcClient) = await ConnectAsync(fragments =>
            [new RpcFaultPdu { CallId = fragments[0].CallId, Status = RpcStatus.AccessDenied }]);
        await using var client = rpcClient;
        await using var server2 = server;

        var keeper = new PingKeeper(exporter, TimeSpan.FromHours(1), _ => throw new InvalidOperationException("No reconnect expected."));
        keeper.Add(1);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);

        Assert.IsType<RpcFaultException>(keeper.LastFailure);
        await keeper.DisposeAsync();
    }

    [Fact]
    public async Task The_background_loop_pings_on_its_own_without_PingNowAsync()
    {
        var pinged = new TaskCompletionSource();
        var (exporter, server, rpcClient) = await ConnectAsync(fragments =>
        {
            pinged.TrySetResult();
            return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = ComplexPingReply(1) }];
        });
        await using var client = rpcClient;
        await using var server2 = server;

        var keeper = new PingKeeper(exporter, TimeSpan.FromMilliseconds(20));
        keeper.Add(1);

        await pinged.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await keeper.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_releases_the_set_with_a_final_ping()
    {
        var (exporter, server, rpcClient) = await ConnectAsync(fragments =>
            [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = ComplexPingReply(0xAB) }]);
        await using var client = rpcClient;
        await using var server2 = server;

        var keeper = new PingKeeper(exporter, TimeSpan.FromHours(1));
        keeper.Add(1);
        keeper.Add(2);
        await keeper.PingNowAsync(TestContext.Current.CancellationToken);

        await keeper.DisposeAsync();

        Assert.Equal(2, server.Requests.Count);
        var final = server.Requests[1];
        Assert.Equal((ushort)2, final.OpNum);
        var expected = ObjectExporter.BuildComplexPingRequest(0xAB, 2, [], [1, 2]);
        Assert.Equal(expected, final.Stub);
    }

    [Fact]
    public async Task Disposing_before_any_ping_sends_nothing()
    {
        var (exporter, server, rpcClient) = await ConnectAsync();
        await using var client = rpcClient;
        await using var server2 = server;

        var keeper = new PingKeeper(exporter, TimeSpan.FromHours(1));
        keeper.Add(1);
        await keeper.DisposeAsync();

        Assert.Empty(server.Requests);
    }

    [Fact]
    public void A_non_positive_period_is_rejected()
    {
        var (clientSide, _) = LoopbackTransport.CreatePair();
        var rpcClient = new RpcClient(clientSide);
        var exporter = new ObjectExporter(rpcClient, new RpcPresentationContext(0, ObjectExporter.Interface, RpcSyntaxId.Ndr20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PingKeeper(exporter, TimeSpan.Zero));
    }
}
