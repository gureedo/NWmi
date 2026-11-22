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

/// <summary>What goes on the wire for CreateInstanceEnum and PutInstance (arrays included), against a fake server.</summary>
public class WmiWriteTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const ulong ServicesOxid = 0x9999;
    private static readonly StdObjRef ServicesStd = new(0, 5, ServicesOxid, 0x1, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private const int OpPutInstance = 14;
    private const int OpCreateInstanceEnum = 18;

    private static async Task<(WbemServicesClient Services, WmiConnection Connection, FakeRpcServer Server)> ConnectAsync()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var exporterConnection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: default);
        var orpc = await WmiOrpcConnection.FromExporterConnectionAsync(
            exporterConnection, ownsConnection: true, ServicesStd, WmiIds.IidWbemServices, security: null, cancellationToken: default);
        var services = new WbemServicesClient(orpc);
        var wmi = WmiConnection.FromServices(services, "server01", @"root\default", new WmiConnectionOptions { UseSmartEnumeration = false, BatchSize = 1 });
        return (services, wmi, server);
    }

    private static NdrWriter OrpcThat()
    {
        var writer = new NdrWriter();
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        return writer;
    }

    private static RpcResponsePdu Reply(RpcRequestPdu request, NdrWriter stub) =>
        new() { CallId = request.CallId, ContextId = request.ContextId, Stub = stub.ToArray() };

    private static WmioProperty Property(string name, WbemType type, ushort index) =>
        new(name, type, false, index, 0, [], new NullAndDefaultFlag(true, false), null, 0);

    private static readonly WmioClass TestClass = new(
        "NWmi_Test", [], [],
        [
            Property("Name", WbemType.String, 0),
            Property("Tags", WbemType.String | WbemType.ArrayFlag, 1),
            Property("Numbers", WbemType.Sint32 | WbemType.ArrayFlag, 2),
            Property("Bytes", WbemType.Uint8 | WbemType.ArrayFlag, 3),
            Property("Flags", WbemType.Boolean | WbemType.ArrayFlag, 4),
            Property("Big", WbemType.Uint64 | WbemType.ArrayFlag, 5),
        ],
        []);

    // ---- CreateInstanceEnum ------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateInstanceEnum_sends_opnum_18_with_the_class_flags_and_no_context()
    {
        var (services, _, server) = await ConnectAsync();
        await using (server)
        {
            var enumStd = new StdObjRef(0, 5, ServicesOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
            var enumObjRef = new StandardObjRef(WmiIds.IidEnumWbemClassObject, enumStd, new DualStringArray([], [])).ToBytes();

            server.CallHandler = fragments =>
            {
                Assert.Equal((ushort)OpCreateInstanceEnum, fragments[0].OpNum);
                var reader = new NdrReader(FakeRpcServer.Join(fragments).AsMemory()[32..]); // skip ORPCTHIS
                Assert.Equal("Win32_Process", reader.ReadBstr().Value);
                Assert.Equal((int)(WbemGenericFlags.ReturnImmediately | WbemGenericFlags.ForwardOnly), reader.ReadInt32());
                Assert.True(reader.ReadMInterfacePointer().IsNull);

                var reply = OrpcThat();
                reply.WriteMInterfacePointer(enumObjRef);
                reply.WriteInt32(0);
                return [Reply(fragments[0], reply)];
            };

            var enumerator = await services.CreateInstanceEnumAsync(
                "Win32_Process", WbemGenericFlags.ReturnImmediately | WbemGenericFlags.ForwardOnly,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(enumerator);
            Assert.Equal(WmiIds.IidEnumWbemClassObject, server.Binds[^1].Contexts[0].AbstractSyntax.Uuid);
        }
    }

    [Fact]
    public async Task CreateInstanceEnum_sends_an_existing_context_as_a_custom_object_reference()
    {
        var (services, _, server) = await ConnectAsync();
        await using (server)
        {
            var enumStd = new StdObjRef(0, 5, ServicesOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
            var enumObjRef = new StandardObjRef(WmiIds.IidEnumWbemClassObject, enumStd, new DualStringArray([], [])).ToBytes();
            CustomObjRef? seen = null;

            server.CallHandler = fragments =>
            {
                var reader = new NdrReader(FakeRpcServer.Join(fragments).AsMemory()[32..]);
                reader.ReadBstr();
                reader.ReadInt32();
                seen = ObjRef.Parse(reader.ReadMInterfacePointer().Value!) as CustomObjRef;

                var reply = OrpcThat();
                reply.WriteMInterfacePointer(enumObjRef);
                reply.WriteInt32(0);
                return [Reply(fragments[0], reply)];
            };

            var context = WmiContext.Empty.SetInt32("__ProviderArchitecture", 64).SetBoolean("__RequiredArchitecture", true);
            await services.CreateInstanceEnumAsync("StdRegProv", context: context, cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(seen);
            Assert.Equal(WmiIds.IidWbemContext, seen.Iid);
            Assert.Equal(context.ToBufferBytes(), seen.ObjectData);
        }
    }

    [Fact]
    public async Task CreateInstanceEnum_failure_status_throws_naming_the_call()
    {
        var (services, _, server) = await ConnectAsync();
        await using (server)
        {
            server.CallHandler = fragments =>
            {
                var reply = OrpcThat();
                reply.WriteMInterfacePointer(null);
                reply.WriteInt32(unchecked((int)0x80041010)); // WBEM_E_INVALID_CLASS
                return [Reply(fragments[0], reply)];
            };

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => services.CreateInstanceEnumAsync("NoSuchClass", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("CreateInstanceEnum", ex.Message, StringComparison.Ordinal);
        }
    }

    // ---- PutInstance -------------------------------------------------------------------------------------------

    /// <summary>Answers PutInstance with success, handing the decoded instance and flags to the caller.</summary>
    private static void CapturePut(FakeRpcServer server, Action<WmioObjectBlock, int> capture, int status = 0)
    {
        server.CallHandler = fragments =>
        {
            Assert.Equal((ushort)OpPutInstance, fragments[0].OpNum);
            var reader = new NdrReader(FakeRpcServer.Join(fragments).AsMemory()[32..]);
            var block = WmiClassObjectCodec.ReadParameter(reader);
            Assert.NotNull(block);
            var flags = reader.ReadInt32();
            capture(block, flags);

            var reply = OrpcThat();
            reply.WriteUniquePointer(true, w => w.WriteMInterfacePointer(null)); // ppCallResult: none
            reply.WriteInt32(status);
            return [Reply(fragments[0], reply)];
        };
    }

    private static WmiInstance NewTestObject() => WmiInstance.FromWire(WmioObjectBlock.ForInstance(WmioInstance.Create(TestClass)), "server01", @"root\default");

    [Fact]
    public async Task PutInstance_sends_array_properties_of_every_element_type()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            WmioInstance? sent = null;
            CapturePut(server, (block, _) => sent = block.Instance);

            var instance = NewTestObject();
            instance["Name"] = "one";
            instance["Tags"] = new object?[] { "a", "ünï ✓", "" };
            instance["Numbers"] = new object?[] { int.MinValue, 0, int.MaxValue };
            instance["Bytes"] = new object?[] { (byte)0, (byte)127, (byte)255 };
            instance["Flags"] = new object?[] { true, false, true };
            instance["Big"] = new object?[] { 0UL, ulong.MaxValue };
            await wmi.PutInstanceAsync(instance, cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(sent);
            Assert.Equal("NWmi_Test", sent.Class.Name);
            Assert.Equal("one", sent.Values["Name"]);
            Assert.Equal(new object?[] { "a", "ünï ✓", "" }, sent.Values["Tags"]);
            Assert.Equal(new object?[] { int.MinValue, 0, int.MaxValue }, sent.Values["Numbers"]);
            Assert.Equal(new object?[] { (byte)0, (byte)127, (byte)255 }, sent.Values["Bytes"]);
            Assert.Equal(new object?[] { true, false, true }, sent.Values["Flags"]);
            Assert.Equal(new object?[] { 0UL, ulong.MaxValue }, sent.Values["Big"]);
        }
    }

    [Fact]
    public async Task PutInstance_sends_empty_and_null_arrays_as_different_things()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            WmioInstance? sent = null;
            CapturePut(server, (block, _) => sent = block.Instance);

            var instance = NewTestObject();
            instance["Tags"] = Array.Empty<object?>();
            instance["Numbers"] = null;
            await wmi.PutInstanceAsync(instance, cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(sent);
            Assert.Empty(Assert.IsType<object?[]>(sent.Values["Tags"]));
            Assert.Null(sent.Values["Numbers"]);
        }
    }

    [Fact]
    public async Task PutInstance_sends_a_large_array_intact()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            WmioInstance? sent = null;
            CapturePut(server, (block, _) => sent = block.Instance);

            var instance = NewTestObject();
            var numbers = Enumerable.Range(0, 5000).Select(i => (object?)i).ToArray();
            var tags = Enumerable.Range(0, 500).Select(i => (object?)$"tag-{i}").ToArray();
            instance["Numbers"] = numbers;
            instance["Tags"] = tags;
            await wmi.PutInstanceAsync(instance, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(numbers, (object?[])sent!.Values["Numbers"]!);
            Assert.Equal(tags, (object?[])sent.Values["Tags"]!);
        }
    }

    [Fact]
    public async Task PutInstance_rejects_an_array_element_of_the_wrong_type_before_sending()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var called = false;
            CapturePut(server, (_, _) => called = true);

            var instance = NewTestObject();
            instance["Numbers"] = new object?[] { 1, "two", 3 };

            await Assert.ThrowsAsync<ArgumentException>(
                () => wmi.PutInstanceAsync(instance, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(called);
        }
    }

    [Theory]
    [InlineData(WbemChangeFlags.CreateOrUpdate, 0)]
    [InlineData(WbemChangeFlags.CreateOnly, 2)]
    [InlineData(WbemChangeFlags.UpdateOnly, 1)]
    public async Task PutInstance_passes_the_change_flags(WbemChangeFlags change, int expected)
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var flags = -1;
            CapturePut(server, (_, f) => flags = f);

            await wmi.PutInstanceAsync(NewTestObject(), change, TestContext.Current.CancellationToken);

            Assert.Equal(expected, flags);
        }
    }

    [Fact]
    public async Task PutInstance_throws_when_the_server_returns_an_error_status()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            CapturePut(server, (_, _) => { }, status: unchecked((int)0x80041002)); // WBEM_E_NOT_FOUND

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => wmi.PutInstanceAsync(NewTestObject(), WbemChangeFlags.UpdateOnly, TestContext.Current.CancellationToken));
            Assert.Contains("PutInstance", ex.Message, StringComparison.Ordinal);
        }
    }

    // ---- per-property qualifiers -------------------------------------------------------------------------------

    [Fact]
    public async Task PutInstance_without_qualifiers_uses_the_no_property_qualifiers_form()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            WmioInstance? sent = null;
            CapturePut(server, (block, _) => sent = block.Instance);

            await wmi.PutInstanceAsync(NewTestObject(), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Null(sent!.PropertyQualifiers);
            Assert.Empty(sent.Qualifiers);
        }
    }

    [Fact]
    public async Task PutInstance_sends_the_property_qualifiers_set_on_the_object()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            WmioInstance? sent = null;
            CapturePut(server, (block, _) => sent = block.Instance);

            var instance = NewTestObject();
            instance["Name"] = "q";
            instance.SetPropertyQualifier("Name", "Description", "the name");
            instance.SetPropertyQualifier("Name", "FLAG", true);
            instance.SetPropertyQualifier("Name", "flag", false); // a qualifier name matches case-insensitively, so this replaces the one above
            instance.SetPropertyQualifier("Numbers", "ValueMap", new[] { "1", "2" });
            instance.SetPropertyQualifier("Numbers", "MaxLen", 255);
            await wmi.PutInstanceAsync(instance, cancellationToken: TestContext.Current.CancellationToken);

            var qualifiers = sent!.PropertyQualifiers!;
            Assert.Equal(["Description", "FLAG"], qualifiers["Name"].Select(q => q.Name));
            Assert.Equal("the name", qualifiers["Name"][0].Value);
            Assert.Equal(WbemType.Boolean, qualifiers["Name"][1].Type);
            Assert.Equal(false, qualifiers["Name"][1].Value);
            Assert.Equal("FLAG", qualifiers["Name"][1].Name);
            Assert.Equal(WbemType.String | WbemType.ArrayFlag, qualifiers["Numbers"][0].Type);
            Assert.Equal(new object?[] { "1", "2" }, qualifiers["Numbers"][0].Value);
            Assert.Equal(WbemType.Sint32, qualifiers["Numbers"][1].Type);
            Assert.Empty(qualifiers["Tags"]); // properties without any get an empty set
            Assert.Equal("q", sent.Values["Name"]); // and the values are unaffected
        }
    }

    [Fact]
    public async Task A_read_object_keeps_its_qualifiers_when_it_is_put_back()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            // An instance as a server would send it: one property with an instance-level qualifier, plus an instance one.
            var original = WmioInstance.Create(
                TestClass,
                new Dictionary<string, object?> { ["Name"] = "n" },
                [new WmioQualifier("Provider", QualifierFlavor.None, WbemType.String, "p")],
                new Dictionary<string, IReadOnlyList<WmioQualifier>>
                {
                    ["Name"] = [new WmioQualifier("Description", QualifierFlavor.None, WbemType.String, "d")],
                });
            var wire = WmioObjectBlock.DecodeEncodingUnit(WmioObjectBlock.ForInstance(original).EncodeEncodingUnit());
            var read = WmiInstance.FromWire(wire, "server01", @"root\default");
            Assert.Equal("d", read.GetPropertyQualifiers("Name")["Description"]);

            WmioInstance? sent = null;
            CapturePut(server, (block, _) => sent = block.Instance);
            read["Name"] = "changed";
            await wmi.PutInstanceAsync(read, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("changed", sent!.Values["Name"]);
            Assert.Equal("Provider", Assert.Single(sent.Qualifiers).Name);
            Assert.Equal("d", sent.PropertyQualifiers!["Name"].Single().Value);
        }
    }

    // ---- EnumerateInstancesAsync -------------------------------------------------------------------------------

    private static byte[] TestObjRef(string name) =>
        WmiClassObjectCodec.ToObjRefBytes(WmioObjectBlock.ForInstance(
            WmioInstance.Create(TestClass, new Dictionary<string, object?> { ["Name"] = name })));

    /// <summary>Answers CreateInstanceEnum with an enumerator that yields the given names, one per Next, and records the call.</summary>
    private static void ServeInstances(FakeRpcServer server, string[] names, Action<string, int> onCreate)
    {
        var enumStd = new StdObjRef(0, 5, ServicesOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
        var enumObjRef = new StandardObjRef(WmiIds.IidEnumWbemClassObject, enumStd, new DualStringArray([], [])).ToBytes();
        var next = 0;
        server.CallHandler = fragments =>
        {
            var reply = OrpcThat();
            switch (fragments[0].OpNum)
            {
                case OpCreateInstanceEnum:
                    var reader = new NdrReader(FakeRpcServer.Join(fragments).AsMemory()[32..]);
                    onCreate(reader.ReadBstr().Value!, reader.ReadInt32());
                    reply.WriteMInterfacePointer(enumObjRef);
                    reply.WriteInt32(0);
                    break;
                case 4: // IEnumWbemClassObject::Next
                    var batch = next < names.Length ? new[] { TestObjRef(names[next++]) } : [];
                    reply.WriteMaxCount(batch.Length);
                    reply.WriteUInt32(0);
                    reply.WriteMaxCount(batch.Length);
                    foreach (var objRef in batch)
                        reply.Struct(w => w.WriteMInterfacePointer(objRef));

                    reply.WriteUInt32((uint)batch.Length);
                    reply.WriteInt32(batch.Length == 0 ? (int)WbemStatus.False : 0);
                    break;
                default:
                    reply.WriteInt32(0);
                    break;
            }

            return [Reply(fragments[0], reply)];
        };
    }

    [Theory]
    [InlineData(false, 0x30)] // ReturnImmediately | ForwardOnly, deep
    [InlineData(true, 0x31)] // and Shallow
    public async Task EnumerateInstancesAsync_streams_the_instances_with_the_depth_flag(bool shallow, int expectedFlags)
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            string? requested = null;
            var flags = -1;
            ServeInstances(server, ["a", "b", "c"], (c, f) => (requested, flags) = (c, f));

            var names = new List<string?>();
            await foreach (var obj in wmi.EnumerateInstancesAsync("NWmi_Test", shallow, TestContext.Current.CancellationToken))
                names.Add((string?)obj["Name"]);

            Assert.Equal("NWmi_Test", requested);
            Assert.Equal(expectedFlags, flags);
            Assert.Equal(["a", "b", "c"], names);
        }
    }

    [Fact]
    public async Task EnumerateInstancesAsync_with_no_instances_yields_nothing()
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            ServeInstances(server, [], (_, _) => { });

            var any = false;
            await foreach (var _ in wmi.EnumerateInstancesAsync("NWmi_Test", cancellationToken: TestContext.Current.CancellationToken))
                any = true;

            Assert.False(any);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Win32 Process")]
    [InlineData("Win32_Process'")]
    [InlineData("1Class")]
    [InlineData("A.B")]
    [InlineData("A;B")]
    public async Task EnumerateInstancesAsync_rejects_a_name_that_is_not_a_class_name_without_connecting(string name)
    {
        var (_, wmi, server) = await ConnectAsync();
        await using (wmi)
        await using (server)
        {
            var bindsBefore = server.Binds.Count;
            Assert.ThrowsAny<ArgumentException>(() => wmi.EnumerateInstancesAsync(name, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(bindsBefore, server.Binds.Count); // nothing was sent
        }
    }
}
