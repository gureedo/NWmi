using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Ndr;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class ObjectExporterCallTests
{
    private const string ServerAlive2Reply =
        "0500 0700 | 00000200 | 0e000000 | 0e00 0a00 | " +
        "0700 3100 2e00 3200 2e00 3300 2e00 3400 0000 0000 | 0a00 ffff 0000 0000 | 00000000 | 00000000";

    [Fact]
    public async Task ServerAlive2_is_called_with_an_empty_stub_on_opnum_5()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
        [
            new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = Hex.Parse(ServerAlive2Reply) },
        ];
        server.Start();
        await using var client = new RpcClient(clientSide);

        var context = (await client.BindAsync([ObjectExporter.Interface], cancellationToken: TestContext.Current.CancellationToken))[0];
        var result = await new ObjectExporter(client, context).ServerAlive2Async(TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)5, request.OpNum);
        Assert.Empty(request.Stub);
        Assert.Null(request.ObjectUuid);
        Assert.Equal(new Version(5, 7), result.Version);
        Assert.Equal("1.2.3.4", Assert.Single(result.Bindings.StringBindings).NetworkAddress);
    }

    [Fact]
    public async Task ResolveOxid2_sends_the_oxid_and_the_protocol_sequences_on_opnum_4()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
        [
            new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = [0x57, 0x00, 0x07, 0x80
                ]
            },
        ];
        server.Start();
        await using var client = new RpcClient(clientSide);

        var context = (await client.BindAsync([ObjectExporter.Interface], cancellationToken: TestContext.Current.CancellationToken))[0];
        var exporter = new ObjectExporter(client, context);

        var ex = await Assert.ThrowsAsync<DcomException>(
            () => exporter.ResolveOxid2Async(0x1122334455667788, [DualStringArray.TcpTowerId], TestContext.Current.CancellationToken));

        Assert.Equal(unchecked((int)0x80070057u), ex.HResult);
        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)4, request.OpNum);
        Assert.Equal(Hex.Parse("8877665544332211 | 0100 0000 | 01000000 | 0700"), request.Stub);
    }

    [Fact]
    public void The_interface_is_the_documented_object_exporter()
    {
        Assert.Equal(Guid.Parse("99fcfec4-5260-101b-bbcb-00aa0021347a"), ObjectExporter.Interface.Uuid);
        Assert.Equal(0, ObjectExporter.Interface.VersionMajor);
        Assert.Equal(0, ObjectExporter.Interface.VersionMinor);
    }
}
