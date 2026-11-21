using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;
using NWmi.Wmi;

namespace NWmi.Tests.Wmi;

public class WbemLevel1LoginClientTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const ulong LoginOxid = 0x4242;
    private static readonly StdObjRef LoginStd = new(0, 5, LoginOxid, 0x1, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static void WriteOrpcThat(NdrWriter writer)
    {
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
    }

    [Fact]
    public async Task NtlmLogin_sends_the_resource_and_locale_as_LPWSTR_and_binds_the_returned_services_object()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.Start();
        await using (server)
        {
            var rpcClient = new RpcClient(clientSide);
            var exporterConnection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: TestContext.Current.CancellationToken);
            var connection = await WmiOrpcConnection.FromExporterConnectionAsync(
                exporterConnection, ownsConnection: true, LoginStd, WmiIds.IidWbemLevel1Login, security: null, cancellationToken: TestContext.Current.CancellationToken);
            var login = WbemLevel1LoginClient.FromConnection(connection);

            // Same OXID as the login object, so NtlmLoginAsync should reuse this connection (a sibling bind)
            // instead of trying to resolve a second OXID.
            var servicesStd = new StdObjRef(0, 5, LoginOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
            var servicesObjRef = new StandardObjRef(WmiIds.IidWbemServices, servicesStd, new DualStringArray([], [])).ToBytes();

            server.CallHandler = fragments =>
            {
                Assert.Equal((ushort)6, fragments[0].OpNum); // NTLMLogin

                var stub = FakeRpcServer.Join(fragments);
                var reader = new NdrReader(stub.AsMemory()[32..]); // skip ORPCTHIS
                var resource = reader.ReadUniquePointer(r => r.ReadConformantVaryingString());
                Assert.Equal(@"root\cimv2", resource.Value);
                var locale = reader.ReadUniquePointer(r => r.ReadConformantVaryingString());
                Assert.True(locale.IsNull);
                Assert.Equal(0, reader.ReadInt32()); // lFlags MUST be 0
                Assert.True(reader.ReadMInterfacePointer().IsNull); // pCtx

                var reply = new NdrWriter();
                WriteOrpcThat(reply);
                reply.WriteMInterfacePointer(servicesObjRef); // ppNamespace
                reply.WriteInt32(0); // WBEM_S_NO_ERROR

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
            };

            var services = await login.NtlmLoginAsync(@"root\cimv2", cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(services);
            // RemUnknown2, IWbemLevel1Login, and a sibling bind for IWbemServices (same OXID as the login object).
            Assert.Equal(3, server.Binds.Count);
            Assert.Equal(WmiIds.IidWbemServices, server.Binds[2].Contexts[0].AbstractSyntax.Uuid);
        }
    }
}
