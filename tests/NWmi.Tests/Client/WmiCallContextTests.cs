using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Options;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;
using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Client;

/// <summary>The public call context: its value semantics, and that every kind of call carries it on the wire.</summary>
public class WmiCallContextTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const ulong ServicesOxid = 0x9999;
    private static readonly StdObjRef ServicesStd = new(0, 5, ServicesOxid, 0x1, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly StdObjRef EnumStd = new(0, 5, ServicesOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));

    private static readonly WmioClass TestClass = new(
        "NWmi_Test", [], [],
        [new WmioProperty("Name", WbemType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)],
        []);

    private static readonly byte[] Arch64 = WmiContext.Empty.SetInt32("__ProviderArchitecture", 64).ToBufferBytes();

    private static WmiCallContext Context64 => WmiCallContext.Empty.With("__ProviderArchitecture", 64);

    // ---- the value type ----------------------------------------------------------------------------------------

    [Fact]
    public void With_returns_a_changed_copy_and_leaves_the_original_alone()
    {
        var a = WmiCallContext.Empty.With("A", 1);
        var b = a.With("B", true).With("C", "text");

        Assert.True(WmiCallContext.Empty.IsEmpty);
        Assert.Equal(new Dictionary<string, object> { ["A"] = 1 }, a.Values);
        Assert.Equal(new Dictionary<string, object> { ["A"] = 1, ["B"] = true, ["C"] = "text" }, b.Values);
    }

    [Fact]
    public void With_replaces_a_value_of_the_same_name_and_moves_it_last()
    {
        var context = WmiCallContext.Empty.With("A", 1).With("B", 2).With("A", 3);

        Assert.Equal(3, context.Values["A"]);
        Assert.Equal(2, context.Values.Count);
    }

    [Fact]
    public void Bad_arguments_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => WmiCallContext.Empty.With("", 1));
        Assert.Throws<ArgumentNullException>(() => WmiCallContext.Empty.With("A", (string)null!));
        Assert.Throws<ArgumentNullException>(() => WmiCallContext.Empty.With(null!, true));
    }

    [Fact]
    public void The_wire_form_matches_the_internal_builder_and_an_empty_context_has_none()
    {
        var wire = WmiCallContext.Empty.With("__ProviderArchitecture", 64).With("__RequiredArchitecture", true).With("L", "MS_409").ToWire();

        Assert.NotNull(wire);
        Assert.Equal(
            WmiContext.Empty.SetInt32("__ProviderArchitecture", 64).SetBoolean("__RequiredArchitecture", true).SetString("L", "MS_409").ToBufferBytes(),
            wire.ToBufferBytes());
        Assert.Null(WmiCallContext.Empty.ToWire());
    }

    [Fact]
    public void Replacing_a_value_puts_it_last_on_the_wire()
    {
        var wire = WmiCallContext.Empty.With("A", 1).With("B", 2).With("A", 3).ToWire();

        Assert.Equal(WmiContext.Empty.SetInt32("B", 2).SetInt32("A", 3).ToBufferBytes(), wire!.ToBufferBytes());
    }

    // ---- on the wire -------------------------------------------------------------------------------------------

    private static async Task<(WmiConnection Connection, FakeRpcServer Server)> ConnectAsync(WmiConnectionOptions? options = null)
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var exporterConnection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: default);
        var orpc = await WmiOrpcConnection.FromExporterConnectionAsync(
            exporterConnection, ownsConnection: true, ServicesStd, WmiIds.IidWbemServices, security: null, cancellationToken: default);
        options ??= new WmiConnectionOptions();
        options.UseSmartEnumeration = false;
        return (WmiConnection.FromServices(new WbemServicesClient(orpc), "server01", @"root\cimv2", options), server);
    }

    private static NdrWriter OrpcThat()
    {
        var writer = new NdrWriter();
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        return writer;
    }

    private static byte[] EnumObjRef() =>
        new StandardObjRef(WmiIds.IidEnumWbemClassObject, EnumStd, new DualStringArray([], [])).ToBytes();

    private static byte[] InstanceObjRef() =>
        WmiClassObjectCodec.ToObjRefBytes(WmioObjectBlock.ForInstance(WmioInstance.Create(TestClass)));

    /// <summary>Records the context argument of each call by opnum and answers just enough for it to succeed.</summary>
    private sealed class Recorder
    {
        public Dictionary<ushort, byte[]?> Context { get; } = [];

        public IReadOnlyList<RpcResponsePdu> Handle(IReadOnlyList<RpcRequestPdu> fragments)
        {
            var opnum = fragments[0].OpNum;
            var reader = new NdrReader(FakeRpcServer.Join(fragments).AsMemory()[32..]); // skip ORPCTHIS
            var reply = OrpcThat();
            switch (opnum)
            {
                case 6: // GetObject(strObjectPath, lFlags, pCtx, ...)
                    reader.ReadBstr();
                    reader.ReadInt32();
                    Context[opnum] = ObjectData(reader.ReadMInterfacePointer().Value);
                    reply.WriteUniquePointer(true, w => w.WriteMInterfacePointer(InstanceObjRef()));
                    reply.WriteMInterfacePointer(null);
                    reply.WriteInt32(0);
                    break;
                case 14: // PutInstance(pInst, lFlags, pCtx, ...)
                    WmiClassObjectCodec.ReadParameter(reader);
                    reader.ReadInt32();
                    Context[opnum] = ObjectData(reader.ReadMInterfacePointer().Value);
                    reply.WriteUniquePointer(true, w => w.WriteMInterfacePointer(null));
                    reply.WriteInt32(0);
                    break;
                case 16: // DeleteInstance(strObjectPath, lFlags, pCtx, ...)
                    reader.ReadBstr();
                    reader.ReadInt32();
                    Context[opnum] = ObjectData(reader.ReadMInterfacePointer().Value);
                    reply.WriteUniquePointer(true, w => w.WriteMInterfacePointer(null));
                    reply.WriteInt32(0);
                    break;
                case 12 or 18: // CreateClassEnum / CreateInstanceEnum(strClass, lFlags, pCtx)
                    reader.ReadBstr();
                    reader.ReadInt32();
                    Context[opnum] = ObjectData(reader.ReadMInterfacePointer().Value);
                    reply.WriteMInterfacePointer(EnumObjRef());
                    reply.WriteInt32(0);
                    break;
                case 20 or 22: // ExecQuery / ExecNotificationQuery(strQueryLanguage, strQuery, lFlags, pCtx)
                    reader.ReadBstr();
                    reader.ReadBstr();
                    reader.ReadInt32();
                    Context[opnum] = ObjectData(reader.ReadMInterfacePointer().Value);
                    reply.WriteMInterfacePointer(EnumObjRef());
                    reply.WriteInt32(0);
                    break;
                case 4: // IEnumWbemClassObject::Next: nothing more
                    reply.WriteMaxCount(0);
                    reply.WriteUInt32(0);
                    reply.WriteMaxCount(0);
                    reply.WriteUInt32(0);
                    reply.WriteInt32((int)WbemStatus.False);
                    break;
                default:
                    reply.WriteInt32(0);
                    break;
            }

            return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
        }

        private static byte[]? ObjectData(byte[]? objRef) =>
            objRef is null ? null : Assert.IsType<CustomObjRef>(ObjRef.Parse(objRef)).ObjectData;
    }

    private static async Task Drain<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var _ in source)
        {
        }
    }

    [Fact]
    public async Task Without_a_context_every_call_sends_a_null_pointer()
    {
        var (wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var recorder = new Recorder();
            server.CallHandler = recorder.Handle;
            var ct = TestContext.Current.CancellationToken;

            await Drain(wmi.QueryAsync("SELECT * FROM NWmi_Test", cancellationToken: ct));
            await wmi.GetInstanceAsync("NWmi_Test.Name=\"a\"", cancellationToken: ct);

            Assert.Equal(2, recorder.Context.Count);
            Assert.All(recorder.Context.Values, c => Assert.Null(c));
        }
    }

    [Fact]
    public async Task A_context_set_on_the_connection_goes_with_queries_enumerations_and_object_calls()
    {
        var (wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var recorder = new Recorder();
            server.CallHandler = recorder.Handle;
            wmi.Context = Context64;
            var ct = TestContext.Current.CancellationToken;

            await Drain(wmi.QueryAsync("SELECT * FROM NWmi_Test", cancellationToken: ct)); // 20
            await Drain(wmi.EnumerateInstancesAsync("NWmi_Test", cancellationToken: ct)); // 18
            await Drain(wmi.EnumerateClassesAsync("NWmi_Test", cancellationToken: ct)); // 12
            var instance = await wmi.GetInstanceAsync("NWmi_Test.Name=\"a\"", cancellationToken: ct); // 6
            await wmi.PutInstanceAsync(instance, cancellationToken: ct); // 14
            await wmi.DeleteInstanceAsync("NWmi_Test.Name=\"x\"", ct); // 16

            foreach (var opnum in new ushort[] { 6, 12, 14, 16, 18, 20 })
            {
                Assert.True(recorder.Context.TryGetValue(opnum, out var sent), $"opnum {opnum} was not called");
                Assert.Equal(Arch64, sent);
            }
        }
    }

    [Fact]
    public async Task A_watch_subscription_carries_the_context()
    {
        var (wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var recorder = new Recorder();
            server.CallHandler = recorder.Handle;
            wmi.Context = Context64;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            // A subscription never ends on its own: read until the notification query has been sent, then cancel.
            var watching = Task.Run(async () =>
            {
                await foreach (var _ in wmi.WatchAsync("SELECT * FROM __InstanceCreationEvent", cancellationToken: cts.Token))
                {
                }
            }, cts.Token);
            while (!recorder.Context.ContainsKey(22))
                await Task.Delay(10, TestContext.Current.CancellationToken);

            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
            Assert.Equal(Arch64, recorder.Context[22]);
        }
    }

    [Fact]
    public async Task The_context_in_the_options_is_where_a_connection_starts()
    {
        var (wmi, server) = await ConnectAsync(new WmiConnectionOptions { Context = Context64 });
        await using (wmi)
        await using (server)
        {
            var recorder = new Recorder();
            server.CallHandler = recorder.Handle;

            Assert.Equal(64, wmi.Context!.Values["__ProviderArchitecture"]);
            await wmi.GetInstanceAsync("NWmi_Test.Name=\"a\"", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(Arch64, recorder.Context[6]);
        }
    }

    [Fact]
    public async Task Changing_or_clearing_the_context_affects_the_next_call_only()
    {
        var (wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var recorder = new Recorder();
            server.CallHandler = recorder.Handle;
            var ct = TestContext.Current.CancellationToken;

            wmi.Context = Context64;
            await wmi.GetInstanceAsync("A=@", cancellationToken: ct);
            Assert.Equal(Arch64, recorder.Context[6]);

            wmi.Context = WmiCallContext.Empty.With("__ProviderArchitecture", 32);
            await wmi.GetInstanceAsync("A=@", cancellationToken: ct);
            Assert.Equal(WmiContext.Empty.SetInt32("__ProviderArchitecture", 32).ToBufferBytes(), recorder.Context[6]);

            wmi.Context = null;
            await wmi.GetInstanceAsync("A=@", cancellationToken: ct);
            Assert.Null(recorder.Context[6]);

            wmi.Context = Context64;
            wmi.Context = WmiCallContext.Empty; // empty is the same as none
            await wmi.GetInstanceAsync("A=@", cancellationToken: ct);
            Assert.Null(recorder.Context[6]);
        }
    }

    [Fact]
    public async Task A_query_keeps_the_context_it_was_started_with()
    {
        var (wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var recorder = new Recorder();
            server.CallHandler = recorder.Handle;
            wmi.Context = Context64;

            var query = wmi.QueryAsync("SELECT * FROM NWmi_Test", cancellationToken: TestContext.Current.CancellationToken);
            wmi.Context = null; // after QueryAsync returned, before anything was read
            await Drain(query);

            Assert.Equal(Arch64, recorder.Context[20]);
        }
    }
}
