using System.Net;
using System.Net.Sockets;
using NWmi.Dcom;
using NWmi.Tests.Rpc;
using NWmi.Transport;

namespace NWmi.Tests.Dcom;

public class ExporterConnectionFallbackTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>A port nothing listens on, so a TCP connect to it fails quickly.</summary>
    private static int UnusedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task A_later_binding_is_used_when_the_first_cannot_be_reached()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var goodPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serving = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await using var server = new FakeRpcServer(new StreamPduTransport(accepted.GetStream()));
            server.Start();
            await server.Completion;
        }, TestContext.Current.CancellationToken);

        var bindings = new DualStringArray(
            [
                new StringBinding(DualStringArray.TcpTowerId, $"127.0.0.1[{UnusedPort()}]"),
                new StringBinding(DualStringArray.TcpTowerId, $"127.0.0.1[{goodPort}]"),
            ],
            []);

        await using var connection = await ExporterConnection.ConnectAsync(bindings, RemUnknownIpid, cancellationToken: TestContext.Current.CancellationToken);

        await connection.DisposeAsync();
        await serving;
    }

    [Fact]
    public async Task All_bindings_unreachable_is_reported_with_every_attempt()
    {
        var bindings = new DualStringArray(
            [
                new StringBinding(DualStringArray.TcpTowerId, $"127.0.0.1[{UnusedPort()}]"),
                new StringBinding(DualStringArray.TcpTowerId, $"127.0.0.1[{UnusedPort()}]"),
            ],
            []);

        var ex = await Assert.ThrowsAsync<DcomException>(() => ExporterConnection.ConnectAsync(bindings, RemUnknownIpid, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
        var aggregate = Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
    }

    [Fact]
    public async Task No_binding_at_all_is_reported_without_attempting_a_connection()
    {
        var ex = await Assert.ThrowsAsync<DcomException>(
            () => ExporterConnection.ConnectAsync(new DualStringArray([], []), RemUnknownIpid, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(ex.InnerException);
    }
}
