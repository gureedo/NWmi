using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;
using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Wmi;

public class WbemServicesClientTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const ulong ServicesOxid = 0x9999;
    private static readonly StdObjRef ServicesStd = new(0, 5, ServicesOxid, 0x1, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static void WriteOrpcThat(NdrWriter writer)
    {
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
    }

    private static async Task<(WbemServicesClient Client, FakeRpcServer Server)> ConnectAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var exporterConnection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: default);
        var connection = await WmiOrpcConnection.FromExporterConnectionAsync(
            exporterConnection, ownsConnection: true, ServicesStd, WmiIds.IidWbemServices, security: null, cancellationToken: default);
        return (new WbemServicesClient(connection), server);
    }

    [Fact]
    public async Task ExecQuery_sends_WQL_and_the_query_text_and_reuses_the_connection_for_the_same_OXID_enumerator()
    {
        var (client, server) = await ConnectAsync();
        await using (server)
        {
            var enumStd = new StdObjRef(0, 5, ServicesOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
            var enumObjRef = new StandardObjRef(WmiIds.IidEnumWbemClassObject, enumStd, new DualStringArray([], [])).ToBytes();

            server.CallHandler = fragments =>
            {
                Assert.Equal((ushort)20, fragments[0].OpNum); // ExecQuery

                var stub = FakeRpcServer.Join(fragments);
                var reader = new NdrReader(stub.AsMemory()[32..]); // skip ORPCTHIS
                Assert.Equal("WQL", reader.ReadBstr().Value);
                Assert.Equal("SELECT * FROM Win32_OperatingSystem", reader.ReadBstr().Value);
                Assert.Equal(0, reader.ReadInt32()); // lFlags
                var ctx = reader.ReadMInterfacePointer();
                Assert.True(ctx.IsNull); // no WmiContext was passed

                var reply = new NdrWriter();
                WriteOrpcThat(reply);
                reply.WriteMInterfacePointer(enumObjRef); // ppEnum
                reply.WriteInt32(0); // WBEM_S_NO_ERROR

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
            };

            var enumerator = await client.ExecQueryAsync("SELECT * FROM Win32_OperatingSystem", cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(enumerator);
            // Same OXID as the services object => a sibling bind reusing the one connection, not a second one.
            Assert.Equal(3, server.Binds.Count); // RemUnknown2, IWbemServices, IEnumWbemClassObject
            Assert.Equal(WmiIds.IidWbemServices, server.Binds[1].Contexts[0].AbstractSyntax.Uuid);
            Assert.Equal(WmiIds.IidEnumWbemClassObject, server.Binds[2].Contexts[0].AbstractSyntax.Uuid);
        }
    }

    [Fact]
    public async Task GetObject_decodes_the_returned_instance_and_a_null_call_result()
    {
        var (client, server) = await ConnectAsync();
        await using (server)
        {
            var @class = new WmioClass(
                "Win32_OperatingSystem", [], [],
                [new WmioProperty("Caption", WbemType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
            var block = WmioObjectBlock.ForInstance(WmioInstance.Create(@class, new Dictionary<string, object?> { ["Caption"] = "Test OS" }));
            var objRef = WmiClassObjectCodec.ToObjRefBytes(block);

            server.CallHandler = fragments =>
            {
                Assert.Equal((ushort)6, fragments[0].OpNum); // GetObject

                var reply = new NdrWriter();
                WriteOrpcThat(reply);
                reply.WriteUniquePointer(true, w => w.WriteMInterfacePointer(objRef)); // ppObject
                reply.WriteMInterfacePointer(null); // ppCallResult
                reply.WriteInt32(0);

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
            };

            var result = await client.GetObjectAsync(@"\\.\root\cimv2:Win32_OperatingSystem", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Null(result.CallResult);
            Assert.Equal("Test OS", result.Object!.Instance!.Values["Caption"]);
        }
    }
}
