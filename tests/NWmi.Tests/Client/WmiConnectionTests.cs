using NWmi.Client;
using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;
using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Client;

public class WmiConnectionTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const ulong ServicesOxid = 0x9999;
    private static readonly StdObjRef ServicesStd = new(0, 5, ServicesOxid, 0x1, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static void WriteOrpcThat(NdrWriter writer)
    {
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
    }

    private static byte[] StatusOnlyReply(int status = 0)
    {
        var writer = new NdrWriter();
        WriteOrpcThat(writer);
        writer.WriteInt32(status);
        return writer.ToArray();
    }

    private static async Task<(WmiConnection Connection, FakeRpcServer Server)> ConnectAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var exporterConnection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: default);
        var connection = await WmiOrpcConnection.FromExporterConnectionAsync(
            exporterConnection, ownsConnection: true, ServicesStd, WmiIds.IidWbemServices, security: null, cancellationToken: default);
        var services = new WbemServicesClient(connection);
        var options = new WmiConnectionOptions { UseSmartEnumeration = false, BatchSize = 1 };
        var wmi = WmiConnection.FromServices(services, "server01", @"root\cimv2", options);
        return (wmi, server);
    }

    private static byte[] SampleProcessObjectRef(string handle)
    {
        var @class = new WmioClass(
            "Win32_Process", [], [],
            [new WmioProperty("Handle", WbemType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
        var block = WmioObjectBlock.ForInstance(WmioInstance.Create(@class, new Dictionary<string, object?> { ["Handle"] = handle }));
        return WmiClassObjectCodec.ToObjRefBytes(block);
    }

    [Fact]
    public async Task QueryAsync_fetches_successive_batches_until_the_server_signals_the_end()
    {
        var (wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var enumStd = new StdObjRef(0, 5, ServicesOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
            var enumObjRef = new StandardObjRef(WmiIds.IidEnumWbemClassObject, enumStd, new DualStringArray([], [])).ToBytes();
            var nextCalls = 0;

            server.CallHandler = fragments =>
            {
                var opnum = fragments[0].OpNum;
                byte[] stub;
                if (opnum == 20) // ExecQuery
                {
                    var reply = new NdrWriter();
                    WriteOrpcThat(reply);
                    reply.WriteMInterfacePointer(enumObjRef);
                    reply.WriteInt32(0);
                    stub = reply.ToArray();
                }
                else if (opnum == 4) // IEnumWbemClassObject::Next
                {
                    nextCalls++;
                    var handle = nextCalls.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var isLast = nextCalls == 2;

                    var reply = new NdrWriter();
                    WriteOrpcThat(reply);
                    reply.WriteMaxCount(1);
                    reply.WriteUInt32(0);
                    reply.WriteMaxCount(1);
                    reply.Struct(w => w.WriteMInterfacePointer(SampleProcessObjectRef(handle)));
                    reply.WriteUInt32(1); // puReturned
                    reply.WriteInt32(isLast ? (int)WbemStatus.False : 0);
                    stub = reply.ToArray();
                }
                else
                    stub = StatusOnlyReply();

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = stub }];
            };

            var handles = new List<string?>();
            await foreach (var obj in wmi.QueryAsync("SELECT * FROM Win32_Process", cancellationToken: TestContext.Current.CancellationToken))
                handles.Add((string?)obj["Handle"]);

            Assert.Equal(["1", "2"], handles);
            Assert.Equal(2, nextCalls);
        }
    }

    [Fact]
    public async Task WatchAsync_keeps_polling_through_a_WBEM_S_FALSE_timeout_instead_of_stopping()
    {
        var (wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var enumStd = new StdObjRef(0, 5, ServicesOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
            var enumObjRef = new StandardObjRef(WmiIds.IidEnumWbemClassObject, enumStd, new DualStringArray([], [])).ToBytes();
            var nextCalls = 0;

            server.CallHandler = fragments =>
            {
                var opnum = fragments[0].OpNum;
                byte[] stub;
                if (opnum == 22) // ExecNotificationQuery
                {
                    var reply = new NdrWriter();
                    WriteOrpcThat(reply);
                    reply.WriteMInterfacePointer(enumObjRef);
                    reply.WriteInt32(0);
                    stub = reply.ToArray();
                }
                else if (opnum == 4) // IEnumWbemClassObject::Next
                {
                    nextCalls++;
                    var reply = new NdrWriter();
                    WriteOrpcThat(reply);
                    if (nextCalls == 2)
                    {
                        // A timed-out poll: no events, but WBEM_S_FALSE must not end the subscription.
                        reply.WriteMaxCount(1);
                        reply.WriteUInt32(0);
                        reply.WriteMaxCount(0);
                        reply.WriteUInt32(0);
                        reply.WriteInt32((int)WbemStatus.False);
                    }
                    else
                    {
                        var handle = nextCalls.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        reply.WriteMaxCount(1);
                        reply.WriteUInt32(0);
                        reply.WriteMaxCount(1);
                        reply.Struct(w => w.WriteMInterfacePointer(SampleProcessObjectRef(handle)));
                        reply.WriteUInt32(1);
                        reply.WriteInt32(0);
                    }

                    stub = reply.ToArray();
                }
                else
                    stub = StatusOnlyReply();

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = stub }];
            };

            var handles = new List<string?>();
            await foreach (var evt in wmi.WatchAsync("SELECT * FROM Win32_ProcessStartTrace", cancellationToken: TestContext.Current.CancellationToken))
            {
                handles.Add((string?)evt["Handle"]);
                if (handles.Count == 2)
                    break; // disposes the async enumerator, which releases the server-side one
            }

            Assert.Equal(["1", "3"], handles); // the empty, WBEM_S_FALSE poll (call 2) yielded nothing but did not stop the stream
            Assert.Equal(3, nextCalls);
        }
    }

    [Fact]
    public async Task InvokeMethodAsync_builds_parameters_from_the_methods_declared_input_signature()
    {
        var (wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            // The method's declared input signature (fetched via GetObject before ExecMethod) names two
            // parameters; only one is supplied, and the other must still be present in what gets sent, with
            // its own declared default (here, none): see the remarks on WmiConnection.BuildParametersBlock.
            var signatureClass = new WmioClass(
                "__PARAMETERS", [], [],
                [
                    new WmioProperty("CommandLine", WbemType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
                    new WmioProperty("CurrentDirectory", WbemType.String, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 0),
                ],
                []);
            var signatureWriter = new WmioWriter();
            WmioObjectBlock.ForClass(signatureClass).Encode(signatureWriter);

            var processClass = new WmioClass(
                "Win32_Process", [], [], [],
                [new WmioMethod("Create", false, 0, signatureWriter.ToArray(), [])]);
            var processObjRef = WmiClassObjectCodec.ToObjRefBytes(WmioObjectBlock.ForClass(processClass));

            var outClass = new WmioClass(
                "__PARAMETERS", [], [],
                [new WmioProperty("ReturnValue", WbemType.Uint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
            var outBlock = WmioObjectBlock.ForInstance(WmioInstance.Create(outClass, new Dictionary<string, object?> { ["ReturnValue"] = 0u }));
            var outObjRef = WmiClassObjectCodec.ToObjRefBytes(outBlock);

            server.CallHandler = fragments =>
            {
                var opnum = fragments[0].OpNum;
                var stub = FakeRpcServer.Join(fragments);
                NdrWriter reply;
                if (opnum == 6) // GetObject: looks up Win32_Process's method signature first
                {
                    var reader = new NdrReader(stub.AsMemory()[32..]);
                    Assert.Equal("Win32_Process", reader.ReadBstr().Value);

                    reply = new NdrWriter();
                    WriteOrpcThat(reply);
                    reply.WriteUniquePointer(true, w => w.WriteMInterfacePointer(processObjRef)); // ppObject
                    reply.WriteMInterfacePointer(null); // ppCallResult
                    reply.WriteInt32(0);
                }
                else
                {
                    Assert.Equal((ushort)24, opnum); // ExecMethod

                    var reader = new NdrReader(stub.AsMemory()[32..]); // skip ORPCTHIS
                    Assert.Equal("Win32_Process", reader.ReadBstr().Value);
                    Assert.Equal("Create", reader.ReadBstr().Value);
                    reader.ReadInt32(); // lFlags
                    Assert.True(reader.ReadMInterfacePointer().IsNull); // pCtx

                    var inParams = WmiClassObjectCodec.ReadParameter(reader);
                    Assert.True(inParams!.IsClass);
                    Assert.Equal("notepad.exe", inParams.Class!.FindProperty("CommandLine")!.DefaultValue);
                    Assert.NotNull(inParams.Class!.FindProperty("CurrentDirectory")); // present even though not supplied
                    Assert.Null(inParams.Class!.FindProperty("CurrentDirectory")!.DefaultValue);

                    reply = new NdrWriter();
                    WriteOrpcThat(reply);
                    reply.WriteUniquePointer(true, w => w.WriteMInterfacePointer(outObjRef)); // ppOutParams
                    reply.WriteMInterfacePointer(null); // ppCallResult
                    reply.WriteInt32(0);
                }

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
            };

            var result = await wmi.InvokeMethodAsync("Win32_Process", "Create", new Dictionary<string, object?> { ["CommandLine"] = "notepad.exe" }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.Equal(0u, result["ReturnValue"]);
        }
    }

    [Theory]
    [InlineData(1, nameof(WbemType.Sint32))]
    [InlineData("x", nameof(WbemType.String))]
    [InlineData(true, nameof(WbemType.Boolean))]
    [InlineData(1.5, nameof(WbemType.Real64))]
    public void InferCimType_covers_common_dotnet_types(object value, string expected) =>
        Assert.Equal(Enum.Parse<WbemType>(expected), WmiConnection.InferCimType(value));

    [Fact]
    public void InferCimType_treats_null_as_string()
    {
        Assert.Equal(WbemType.String, WmiConnection.InferCimType(null));
    }

    [Fact]
    public void InferCimType_rejects_a_type_it_cannot_map()
    {
        Assert.Throws<NotSupportedException>(() => WmiConnection.InferCimType(new object()));
    }

    [Fact]
    public void BuildParametersBlock_names_the_synthetic_class___PARAMETERS()
    {
        // A real server sends ExecMethod's pInParams as a class whose property values are its own defaults,
        // not an instance of __PARAMETERS; see the remarks on WmiConnection.BuildParametersBlock.
        var block = WmiConnection.BuildParametersBlock(new Dictionary<string, object?> { ["A"] = 1, ["B"] = "two" });

        Assert.True(block.IsClass);
        Assert.Equal("__PARAMETERS", block.Class!.Name);
        Assert.Equal(1, block.Class.FindProperty("A")!.DefaultValue);
        Assert.Equal("two", block.Class.FindProperty("B")!.DefaultValue);
    }

    [Fact]
    public void BuildParametersBlock_round_trips_through_encode_and_decode()
    {
        var block = WmiConnection.BuildParametersBlock(new Dictionary<string, object?> { ["CommandLine"] = "notepad.exe" });
        var decoded = WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit());

        Assert.True(decoded.IsClass);
        Assert.Equal("__PARAMETERS", decoded.Class!.Name);
        Assert.Equal("notepad.exe", decoded.Class.FindProperty("CommandLine")!.DefaultValue);
    }

    [Fact]
    public void InferCimType_maps_arrays_and_sequences_to_the_element_type_with_the_array_flag()
    {
        Assert.Equal(WbemType.String.AsArray(), WmiConnection.InferCimType(new[] { "a", "b" }));
        Assert.Equal(WbemType.Uint8.AsArray(), WmiConnection.InferCimType(new byte[] { 1, 2 }));
        Assert.Equal(WbemType.Sint32.AsArray(), WmiConnection.InferCimType(new List<int> { 1 }));
        Assert.Equal(WbemType.Uint32.AsArray(), WmiConnection.InferCimType(Enumerable.Range(1, 3).Select(i => (uint)i)));
        Assert.Equal(WbemType.Boolean.AsArray(), WmiConnection.InferCimType(Array.Empty<bool>()));
    }

    [Fact]
    public void InferCimType_types_an_untyped_sequence_by_its_first_element()
    {
        Assert.Equal(WbemType.Sint64.AsArray(), WmiConnection.InferCimType(new object?[] { null, 5L }));
        Assert.Throws<NotSupportedException>(() => WmiConnection.InferCimType(Array.Empty<object>()));
        Assert.Throws<NotSupportedException>(() => WmiConnection.InferCimType(new object?[] { null }));
        Assert.Throws<NotSupportedException>(() => WmiConnection.InferCimType(new[] { new[] { 1 } })); // arrays of arrays
        Assert.Throws<NotSupportedException>(() => WmiConnection.InferCimType(new[] { new object() }));
    }

    [Fact]
    public void BuildParametersBlock_sends_an_array_with_its_element_type_in_CIMTYPE()
    {
        var block = WmiConnection.BuildParametersBlock(new Dictionary<string, object?>
        {
            ["Names"] = new[] { "a", "bb" },
            ["Data"] = new byte[] { 1, 2, 3 },
            ["Ids"] = new List<int> { 7, 8 },
        });
        var decoded = WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit()).Class!;

        var names = decoded.FindProperty("Names")!;
        Assert.Equal(WbemType.String.AsArray(), names.Type);
        Assert.Equal(new object?[] { "a", "bb" }, (object?[])names.DefaultValue!);
        Assert.Equal("string", names.Qualifiers.Single(q => q.Name == "CIMTYPE").Value);
        Assert.Equal(new object?[] { (byte)1, (byte)2, (byte)3 }, (object?[])decoded.FindProperty("Data")!.DefaultValue!);
        Assert.Equal("uint8", decoded.FindProperty("Data")!.Qualifiers.Single(q => q.Name == "CIMTYPE").Value);
        Assert.Equal(new object?[] { 7, 8 }, (object?[])decoded.FindProperty("Ids")!.DefaultValue!);
    }

    [Fact]
    public void BuildParametersBlock_sends_an_empty_array_as_an_empty_array()
    {
        var block = WmiConnection.BuildParametersBlock(new Dictionary<string, object?> { ["Values"] = Array.Empty<string>() });
        var property = WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit()).Class!.FindProperty("Values")!;

        Assert.Equal(WbemType.String.AsArray(), property.Type);
        var values = Assert.IsType<object?[]>(property.DefaultValue);
        Assert.Empty(values);
    }

    private static WmioClass Signature(params (string Name, WbemType Type)[] parameters) => new(
        "__PARAMETERS", [], [],
        [.. parameters.Select((p, i) => new WmioProperty(p.Name, p.Type, false, (ushort)i, 0, [], new NullAndDefaultFlag(true, false), null, 0))],
        []);

    [Fact]
    public void BuildParametersBlock_coerces_each_element_to_the_declared_element_type()
    {
        var signature = Signature(("hDefKey", WbemType.Uint32), ("uValue", WbemType.Uint8.AsArray()), ("sValue", WbemType.String.AsArray()),
            ("lValue", WbemType.Sint64.AsArray()));

        var block = WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?>
            {
                ["uValue"] = new[] { 1, 2, 255 }, // ints for uint8
                ["sValue"] = new List<object> { "x", 5 }, // anything printable for a string
                ["lValue"] = new short[] { -1, 2 },
            },
            signature);
        var decoded = WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit()).Class!;

        Assert.Equal(new object?[] { (byte)1, (byte)2, (byte)255 }, (object?[])decoded.FindProperty("uValue")!.DefaultValue!);
        Assert.Equal(new object?[] { "x", "5" }, (object?[])decoded.FindProperty("sValue")!.DefaultValue!);
        Assert.Equal(new object?[] { -1L, 2L }, (object?[])decoded.FindProperty("lValue")!.DefaultValue!);
        Assert.Null(decoded.FindProperty("hDefKey")!.DefaultValue); // declared, not supplied: stays null
    }

    [Fact]
    public void BuildParametersBlock_refuses_a_scalar_for_an_array_parameter_and_an_element_that_does_not_fit()
    {
        var signature = Signature(("uValue", WbemType.Uint8.AsArray()), ("Name", WbemType.String));

        Assert.Throws<ArgumentException>(() => WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["uValue"] = 5 }, signature));
        Assert.Throws<ArgumentException>(() => WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["uValue"] = "text" }, signature));
        Assert.Throws<OverflowException>(() => WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["uValue"] = new[] { 300 } }, signature));
    }

    [Fact]
    public void BuildParametersBlock_sends_a_null_array_as_null()
    {
        var signature = Signature(("sValue", WbemType.String.AsArray()));

        var block = WmiConnection.BuildParametersBlock(new Dictionary<string, object?> { ["sValue"] = null }, signature);

        Assert.Null(WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit()).Class!.FindProperty("sValue")!.DefaultValue);
    }

    private static WmiObject StartupInfo(ushort showWindow, uint priorityClass, string? title = null)
    {
        var @class = new WmioClass(
            "Win32_ProcessStartup", [], [],
            [
                new WmioProperty("ShowWindow", WbemType.Uint16, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
                new WmioProperty("PriorityClass", WbemType.Uint32, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 0),
                new WmioProperty("Title", WbemType.String, false, 2, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            ],
            []);
        var obj = new WmiObject(WmioObjectBlock.ForClass(@class), "server01", @"root\cimv2");
        obj["ShowWindow"] = showWindow;
        obj["PriorityClass"] = priorityClass;
        obj["Title"] = title;
        return obj;
    }

    private static WmioClass CreateSignature() => Signature(
        ("CommandLine", WbemType.String), ("CurrentDirectory", WbemType.String), ("ProcessStartupInformation", WbemType.Object));

    [Fact]
    public void InferCimType_maps_a_WmiObject_to_an_embedded_object()
    {
        Assert.Equal(WbemType.Object, WmiConnection.InferCimType(StartupInfo(0, 0)));
        Assert.Equal(WbemType.Object.AsArray(), WmiConnection.InferCimType(new[] { StartupInfo(0, 0), StartupInfo(1, 1) }));
    }

    [Fact]
    public void BuildParametersBlock_sends_a_WmiObject_as_an_embedded_instance_of_its_class()
    {
        var block = WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?>
            {
                ["CommandLine"] = "ping 127.0.0.1",
                ["ProcessStartupInformation"] = StartupInfo(0, 64, "hidden"),
            },
            CreateSignature());

        var decoded = WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit()).Class!;
        var embedded = Assert.IsType<WmioObjectBlock>(decoded.FindProperty("ProcessStartupInformation")!.DefaultValue);

        Assert.False(embedded.IsClass);
        Assert.Equal("Win32_ProcessStartup", embedded.Instance!.Class.Name);
        Assert.Equal((ushort)0, embedded.Instance.Values["ShowWindow"]);
        Assert.Equal(64u, embedded.Instance.Values["PriorityClass"]);
        Assert.Equal("hidden", embedded.Instance.Values["Title"]);
        Assert.Equal("ping 127.0.0.1", decoded.FindProperty("CommandLine")!.DefaultValue);
        Assert.Null(decoded.FindProperty("CurrentDirectory")!.DefaultValue); // declared, not supplied
    }

    [Fact]
    public void BuildParametersBlock_names_the_embedded_class_in_the_inferred_parameters_CIMTYPE()
    {
        var block = WmiConnection.BuildParametersBlock(new Dictionary<string, object?> { ["ProcessStartupInformation"] = StartupInfo(1, 2) });

        var property = WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit()).Class!.FindProperty("ProcessStartupInformation")!;

        Assert.Equal(WbemType.Object, property.Type);
        Assert.Equal("object:Win32_ProcessStartup", property.Qualifiers.Single(q => q.Name == "CIMTYPE").Value);
        Assert.IsType<WmioObjectBlock>(property.DefaultValue);
    }

    [Fact]
    public void BuildParametersBlock_sends_an_array_of_embedded_objects()
    {
        var signature = Signature(("Startups", WbemType.Object.AsArray()));

        var block = WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["Startups"] = new List<WmiObject> { StartupInfo(0, 1), StartupInfo(2, 3) } }, signature);

        var elements = (object?[])WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit()).Class!.FindProperty("Startups")!.DefaultValue!;
        Assert.Equal(
            [(ushort)0, (ushort)2],
            elements.Cast<WmioObjectBlock>().Select(b => b.Instance!.Values["ShowWindow"]));
    }

    [Fact]
    public void BuildParametersBlock_refuses_a_value_that_is_not_an_object_for_an_embedded_object_parameter()
    {
        Assert.Throws<ArgumentException>(() => WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["ProcessStartupInformation"] = "hidden" }, CreateSignature()));
        Assert.Throws<NotSupportedException>(() => WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["Startups"] = Array.Empty<WmiObject>() }));
    }

    [Fact]
    public async Task A_path_qualified_with_machine_and_namespace_is_cut_down_to_the_part_the_server_takes()
    {
        var (wmi, server) = await ConnectAsync();
        await using (server)
        await using (wmi)
        {
            const string instance = "Win32_Process.Handle=\"4\"";
            Assert.Equal("Win32_Process", wmi.ToServerPath("Win32_Process"));
            Assert.Equal(instance, wmi.ToServerPath(instance));
            Assert.Equal(instance, wmi.ToServerPath("\\\\anything\\root\\cimv2:" + instance));
            Assert.Equal(instance, wmi.ToServerPath("//./ROOT/CIMV2:" + instance)); // slashes and case
            Assert.Equal("Win32_WmiSetting=@", wmi.ToServerPath("root\\cimv2:Win32_WmiSetting=@"));
            Assert.Equal("not a path at all =", wmi.ToServerPath("not a path at all ="));
            Assert.Throws<ArgumentException>(() => wmi.ToServerPath("\\\\anything\\root\\default:Win32_Process.Handle=1"));
        }
    }

    [Fact]
    public async Task Association_queries_are_built_from_the_instance_path_and_the_filters_given()
    {
        var (wmi, server) = await ConnectAsync();
        await using (server)
        await using (wmi)
        {
            Assert.Equal("ASSOCIATORS OF {Win32_Service.Name=\"WinRM\"}",
                wmi.AssociationQuery("ASSOCIATORS", "\\\\host\\root\\cimv2:Win32_Service.Name='WinRM'"));
            Assert.Equal(
                "ASSOCIATORS OF {Win32_Directory.Name=\"C:\\\\Windows\"} WHERE AssocClass = Win32_SubDirectory Role = PartComponent",
                wmi.AssociationQuery("ASSOCIATORS", "Win32_Directory.Name=\"C:\\\\Windows\"",
                    ("AssocClass", "Win32_SubDirectory", "a"), ("ResultClass", null, "b"), ("Role", "PartComponent", "c")));
            Assert.Equal("REFERENCES OF {Win32_OperatingSystem=@} WHERE ResultClass = CIM_Dependency",
                wmi.AssociationQuery("REFERENCES", "Win32_OperatingSystem=@", ("ResultClass", "CIM_Dependency", "a")));
        }
    }

    [Fact]
    public async Task Association_queries_refuse_a_class_path_and_a_filter_that_is_not_an_identifier()
    {
        var (wmi, server) = await ConnectAsync();
        await using (server)
        await using (wmi)
        {
            Assert.Throws<ArgumentException>(() => wmi.GetAssociatorsAsync("Win32_Service", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Throws<ArgumentException>(() => wmi.GetReferencesAsync("not a path =", cancellationToken: TestContext.Current.CancellationToken));
            var ex = Assert.Throws<ArgumentException>(() =>
                wmi.GetAssociatorsAsync("Win32_Service.Name=\"x\"", resultClass: "Win32_Service Role = x",
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("resultClass", ex.ParamName);
        }
    }
}
