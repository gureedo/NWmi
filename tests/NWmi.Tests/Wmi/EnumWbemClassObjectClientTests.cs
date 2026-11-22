using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;
using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Wmi;

public class EnumWbemClassObjectClientTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly StdObjRef EnumStd = new(0, 5, 0x1234, 0x5678, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static byte[] SampleObjectRef(string name)
    {
        var @class = new WmioClass(
            "TestClass", [], [], [new WmioProperty("Name", WbemType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
        var block = WmioObjectBlock.ForInstance(WmioInstance.Create(@class, new Dictionary<string, object?> { ["Name"] = name }));
        return WmiClassObjectCodec.ToObjRefBytes(block);
    }

    private static async Task<(EnumWbemClassObjectClient Client, FakeRpcServer Server)> ConnectAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var exporterConnection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: default);
        var connection = await WmiOrpcConnection.FromExporterConnectionAsync(
            exporterConnection, ownsConnection: true, EnumStd, WmiIds.IidEnumWbemClassObject, security: null, cancellationToken: default);
        return (new EnumWbemClassObjectClient(connection), server);
    }

    [Fact]
    public async Task Next_decodes_a_two_element_array_of_deferred_interface_pointers_in_order()
    {
        var (client, server) = await ConnectAsync();
        await using (server)
        {
            var objRef1 = SampleObjectRef("first");
            var objRef2 = SampleObjectRef("second");

            server.CallHandler = fragments =>
            {
                Assert.Equal((ushort)4, fragments[0].OpNum); // Next

                var reply = new NdrWriter();
                WriteOrpcThat(reply); // ORPCTHAT: flags 0, no extensions
                reply.WriteMaxCount(2); // apObjects max_count
                reply.WriteUInt32(0); // offset
                reply.WriteMaxCount(2); // actual_count
                reply.Struct(w =>
                {
                    // Two unique pointers in one array: referents inline, pointees deferred together (C706
                    // 14.3.12) - this is what actually exercises EnumWbemClassObjectClient.NextAsync's use
                    // of reader.Struct around the element loop, which a one-element array would not.
                    w.WriteMInterfacePointer(objRef1);
                    w.WriteMInterfacePointer(objRef2);
                });
                reply.WriteUInt32(2); // puReturned
                reply.WriteInt32(0); // HRESULT: WBEM_S_NO_ERROR

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
            };

            var result = await client.NextAsync(2, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.HasMore);
            Assert.Equal(2, result.Objects.Count);
            Assert.Equal("first", result.Objects[0]!.Instance!.Values["Name"]);
            Assert.Equal("second", result.Objects[1]!.Instance!.Values["Name"]);
        }
    }

    [Fact]
    public async Task Next_reports_no_more_objects_on_WBEM_S_FALSE()
    {
        var (client, server) = await ConnectAsync();
        await using (server)
        {
            server.CallHandler = fragments =>
            {
                var reply = new NdrWriter();
                WriteOrpcThat(reply);
                reply.WriteMaxCount(5);
                reply.WriteUInt32(0);
                reply.WriteMaxCount(0);
                reply.WriteUInt32(0); // puReturned
                reply.WriteInt32((int)WbemStatus.False);

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
            };

            var result = await client.NextAsync(5, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(result.HasMore);
            Assert.Empty(result.Objects);
        }
    }

    [Fact]
    public async Task A_WBEM_E_error_throws_WmiException()
    {
        var (client, server) = await ConnectAsync();
        await using (server)
        {
            server.CallHandler = fragments =>
            {
                var reply = new NdrWriter();
                WriteOrpcThat(reply);
                reply.WriteInt32(unchecked((int)WbemStatus.InvalidNamespace));
                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
            };

            var ex = await Assert.ThrowsAsync<WmiException>(() => client.ResetAsync(TestContext.Current.CancellationToken));
            Assert.Equal("InvalidNamespace", ex.StatusName);
        }
    }

    /// <summary>ORPCTHAT with no extensions: flags, then a null extensions pointer (MS-DCOM 2.2.13.4).</summary>
    private static void WriteOrpcThat(NdrWriter writer)
    {
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
    }
}
