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

/// <summary>Calls the server answers with an IWbemCallResult: the result has to be read from it, and it has to be released.</summary>
public class WmiCallResultTests
{
    private const ushort OpOpenNamespace = 3;
    private const ushort OpGetObject = 6;
    private const ushort OpDeleteInstance = 16;
    private const ushort OpExecMethod = 24;
    private const ushort OpGetResultObject = 3;
    private const ushort OpGetResultServices = 5;
    private const ushort OpGetCallStatus = 6;
    private const ushort OpRemRelease = 5;
    private const int NotFound = unchecked((int)0x80041002);

    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const ulong Oxid = 0x9999;
    private static readonly StdObjRef ServicesStd = new(0, 5, Oxid, 0x1, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly StdObjRef CallResultStd = new(0, 5, Oxid, 0x2, Guid.Parse("cccccccc-dddd-eeee-ffff-000000000000"));
    private static readonly StdObjRef OtherServicesStd = new(0, 5, Oxid, 0x3, Guid.Parse("dddddddd-eeee-ffff-0000-111111111111"));

    private static byte[] CallResultRef() =>
        new StandardObjRef(WmiIds.IidWbemCallResult, CallResultStd, new DualStringArray([], [])).ToBytes();

    private static byte[] InstanceRef()
    {
        var @class = new WmioClass(
            "NWmi_Test", [], [],
            [new WmioProperty("Name", WbemType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
        return WmiClassObjectCodec.ToObjRefBytes(
            WmioObjectBlock.ForInstance(WmioInstance.Create(@class, new Dictionary<string, object?> { ["Name"] = "found" })));
    }

    private static NdrWriter Reply()
    {
        var writer = new NdrWriter();
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        return writer;
    }

    private static byte[] TwoSlots(byte[]? first, byte[]? second)
    {
        var reply = Reply();
        WriteSlot(reply, first);
        WriteSlot(reply, second);
        reply.WriteInt32(0);
        return reply.ToArray();
    }

    private static void WriteSlot(NdrWriter writer, byte[]? objRef)
    {
        if (objRef is null)
            writer.WriteMInterfacePointer(null);
        else
            writer.WriteUniquePointer(true, w => w.WriteMInterfacePointer(objRef));
    }

    private static byte[] PointerAndStatus(byte[]? objRef, int status)
    {
        var reply = Reply();
        reply.WriteMInterfacePointer(objRef);
        reply.WriteInt32(status);
        return reply.ToArray();
    }

    private static byte[] CallStatus(int callStatus, int status)
    {
        var reply = Reply();
        reply.WriteInt32(callStatus);
        reply.WriteInt32(status);
        return reply.ToArray();
    }

    /// <summary>A connection whose server sends IWbemServices calls to one handler and IWbemCallResult calls to the other.</summary>
    private static async Task<(WmiConnection Connection, FakeRpcServer Server)> ConnectAsync(
        Func<ushort, byte[]> onServices, Func<ushort, byte[]> onCallResult)
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
        {
            var request = fragments[0];
            var stub = request.ObjectUuid == RemUnknownIpid ? ReleaseReply()
                : request.ObjectUuid == CallResultStd.Ipid ? onCallResult(request.OpNum)
                : onServices(request.OpNum);
            return [new RpcResponsePdu { CallId = request.CallId, ContextId = request.ContextId, Stub = stub }];
        };
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var exporter = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: default);
        var orpc = await WmiOrpcConnection.FromExporterConnectionAsync(
            exporter, ownsConnection: true, ServicesStd, WmiIds.IidWbemServices, security: null, cancellationToken: default);
        var options = new WmiConnectionOptions { Timeout = TimeSpan.FromSeconds(1) };
        return (WmiConnection.FromServices(new WbemServicesClient(orpc), "server01", @"root\cimv2", options), server);

        static byte[] ReleaseReply()
        {
            var reply = Reply();
            reply.WriteInt32(0);
            return reply.ToArray();
        }
    }

    private static int Releases(FakeRpcServer server) =>
        server.Requests.Count(r => r.ObjectUuid == RemUnknownIpid && r.OpNum == OpRemRelease);

    [Fact]
    public async Task GetObject_reads_the_object_from_the_call_result_and_releases_it()
    {
        var (wmi, server) = await ConnectAsync(
            _ => TwoSlots(null, CallResultRef()),
            op =>
            {
                Assert.Equal(OpGetResultObject, op);
                return PointerAndStatus(InstanceRef(), 0);
            });
        await using (wmi)
        await using (server)
        {
            var found = await wmi.GetObjectAsync("NWmi_Test.Name=\"found\"", WbemGenericFlags.ReturnImmediately, TestContext.Current.CancellationToken);

            Assert.Equal("found", found!["Name"]);
            Assert.Equal(1, Releases(server));
        }
    }

    [Fact]
    public async Task GetObject_releases_a_call_result_that_came_with_the_object()
    {
        var (wmi, server) = await ConnectAsync(
            _ => TwoSlots(InstanceRef(), CallResultRef()),
            _ => throw new InvalidOperationException("The call result should not be asked for anything."));
        await using (wmi)
        await using (server)
        {
            var found = await wmi.GetObjectAsync("NWmi_Test.Name=\"found\"", cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(found);
            Assert.Equal(1, Releases(server));
        }
    }

    [Fact]
    public async Task A_synchronous_GetObject_does_not_offer_the_server_a_call_result_slot()
    {
        var (wmi, server) = await ConnectAsync(
            _ => TwoSlots(InstanceRef(), null),
            _ => PointerAndStatus(InstanceRef(), 0));
        await using (wmi)
        await using (server)
        {
            var token = TestContext.Current.CancellationToken;
            await wmi.GetObjectAsync("NWmi_Test", cancellationToken: token);
            await wmi.GetObjectAsync("NWmi_Test", WbemGenericFlags.ReturnImmediately, token);

            var sent = server.Requests.Where(r => r.ObjectUuid == ServicesStd.Ipid && r.OpNum == OpGetObject).ToList();
            Assert.Equal(2, sent.Count);
            Assert.Equal(sent[1].Stub.Length - 4, sent[0].Stub.Length); // a null pointer, where an empty slot is two words
        }
    }

    [Fact]
    public async Task GetObject_reports_the_error_the_call_result_holds()
    {
        var (wmi, server) = await ConnectAsync(
            _ => TwoSlots(null, CallResultRef()),
            _ => PointerAndStatus(null, NotFound));
        await using (wmi)
        await using (server)
        {
            var ex = await Assert.ThrowsAsync<WmiException>(() =>
                wmi.GetObjectAsync("NWmi_Test.Name=\"gone\"", WbemGenericFlags.ReturnImmediately, TestContext.Current.CancellationToken));

            Assert.Equal(NotFound, ex.HResult);
            Assert.Equal(1, Releases(server));
        }
    }

    [Fact]
    public async Task A_method_still_running_is_waited_for_until_it_finishes()
    {
        var polls = 0;
        var (wmi, server) = await ConnectAsync(
            op =>
            {
                Assert.Equal(OpExecMethod, op);
                return TwoSlots(null, CallResultRef());
            },
            op =>
            {
                Assert.Equal(OpGetResultObject, op);
                return ++polls < 3 ? PointerAndStatus(null, (int)WbemStatus.Timedout) : PointerAndStatus(InstanceRef(), 0);
            });
        await using (wmi)
        await using (server)
        {
            var result = await wmi.InvokeMethodAsync("Win32_Process", "Slow", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("found", result!["Name"]);
            Assert.Equal(3, polls);
            Assert.Equal(1, Releases(server));
        }
    }

    [Fact]
    public async Task A_running_method_can_be_cancelled_between_waits()
    {
        var (wmi, server) = await ConnectAsync(
            _ => TwoSlots(null, CallResultRef()),
            _ =>
            {
                Thread.Sleep(20);
                return PointerAndStatus(null, (int)WbemStatus.Timedout);
            });
        await using (wmi)
        await using (server)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                wmi.InvokeMethodAsync("Win32_Process", "Slow", cancellationToken: cancel.Token));
        }
    }

    [Fact]
    public async Task A_cancellation_never_loses_a_reference_the_server_has_handed_out()
    {
        var (wmi, server) = await ConnectAsync(
            op =>
            {
                Assert.Equal(OpDeleteInstance, op);
                Thread.Sleep(300);
                var reply = Reply();
                WriteSlot(reply, CallResultRef());
                reply.WriteInt32(0);
                return reply.ToArray();
            },
            _ => throw new InvalidOperationException("The call was cancelled, so its result should not be asked for."));
        await using (wmi)
        await using (server)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wmi.DeleteInstanceAsync("NWmi_Test.Name=\"x\"", cancel.Token));

            Assert.Equal(1, Releases(server));
        }
    }

    [Fact]
    public async Task A_call_that_is_cancelled_before_it_is_sent_never_reaches_the_server()
    {
        var (wmi, server) = await ConnectAsync(
            _ => throw new InvalidOperationException("Nothing should have been sent."),
            _ => throw new InvalidOperationException("Nothing should have been sent."));
        await using (wmi)
        await using (server)
        {
            using var cancel = new CancellationTokenSource();
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wmi.DeleteInstanceAsync("NWmi_Test.Name=\"x\"", cancel.Token));
        }
    }

    [Fact]
    public async Task A_method_that_fails_on_the_server_reports_its_error()
    {
        var (wmi, server) = await ConnectAsync(
            _ => TwoSlots(null, CallResultRef()),
            _ => PointerAndStatus(null, NotFound));
        await using (wmi)
        await using (server)
        {
            var ex = await Assert.ThrowsAsync<WmiException>(() =>
                wmi.InvokeMethodAsync("Win32_Process", "Gone", cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(NotFound, ex.HResult);
        }
    }

    [Fact]
    public async Task DeleteInstance_keeps_waiting_while_the_call_is_pending_then_reports_its_outcome()
    {
        var polls = 0;
        var (wmi, server) = await ConnectAsync(
            op =>
            {
                Assert.Equal(OpDeleteInstance, op);
                var reply = Reply();
                WriteSlot(reply, CallResultRef());
                reply.WriteInt32(0);
                return reply.ToArray();
            },
            op =>
            {
                Assert.Equal(OpGetCallStatus, op);
                return ++polls < 3 ? CallStatus(0, (int)WbemStatus.Timedout) : CallStatus(NotFound, 0);
            });
        await using (wmi)
        await using (server)
        {
            var ex = await Assert.ThrowsAsync<WmiException>(() =>
                wmi.DeleteInstanceAsync("NWmi_Test.Name=\"x\"", TestContext.Current.CancellationToken));

            Assert.Equal(NotFound, ex.HResult);
            Assert.Equal(3, polls);
        }
    }

    [Fact]
    public async Task OpenNamespace_takes_the_namespace_from_the_call_result()
    {
        var services = new StandardObjRef(WmiIds.IidWbemServices, OtherServicesStd, new DualStringArray([], [])).ToBytes();
        var (wmi, server) = await ConnectAsync(
            op =>
            {
                Assert.Equal(OpOpenNamespace, op);
                return TwoSlots(null, CallResultRef());
            },
            op =>
            {
                Assert.Equal(OpGetResultServices, op);
                return PointerAndStatus(services, 0);
            });
        await using (wmi)
        await using (server)
        {
            var child = await wmi.OpenNamespaceAsync("sub", TestContext.Current.CancellationToken);
            await using (child) Assert.Equal(1, Releases(server));
        }
    }

    [Fact]
    public void The_rpc_timeout_reaches_the_rpc_client_options()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new WmiConnectionOptions().EffectiveRpcClientOptions.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(3), new WmiConnectionOptions { RpcTimeout = TimeSpan.FromMinutes(3) }.EffectiveRpcClientOptions.Timeout);
    }

    [Fact]
    public void The_rpc_timeout_stays_above_the_server_timeout_unless_it_is_set()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new WmiConnectionOptions().EffectiveRpcTimeout);
        Assert.Equal(TimeSpan.FromSeconds(150), new WmiConnectionOptions { Timeout = TimeSpan.FromMinutes(2) }.EffectiveRpcTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), new WmiConnectionOptions { RpcTimeout = TimeSpan.FromMinutes(10) }.EffectiveRpcTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, new WmiConnectionOptions { Timeout = Timeout.InfiniteTimeSpan }.EffectiveRpcTimeout);
    }

    // ---- method parameters -------------------------------------------------------------------------------------

    private static readonly WmioClass Signature = new(
        "__PARAMETERS", [], [],
        [
            new WmioProperty("Reason", WbemType.Uint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("When", WbemType.DateTime, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 4),
        ],
        []);

    private static WmioClass EncodeAndDecode(WmioObjectBlock block)
    {
        var writer = new WmioWriter();
        block.Encode(writer);
        return WmioObjectBlock.Decode(new WmioReader(writer.ToArray())).Class!;
    }

    [Fact]
    public void An_explicit_null_parameter_is_sent_as_null()
    {
        var withSignature = EncodeAndDecode(WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["Reason"] = null }, Signature));
        var inferred = EncodeAndDecode(WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["Reason"] = null }));

        Assert.True(withSignature.FindProperty("Reason")!.NdFlag.IsNull);
        Assert.True(inferred.FindProperty("Reason")!.NdFlag.IsNull);
    }

    [Fact]
    public void Dates_and_intervals_are_sent_in_dmtf_form_with_or_without_a_signature()
    {
        var moment = new DateTimeOffset(2026, 11, 4, 10, 20, 30, TimeSpan.FromHours(-5)).AddTicks(1234560);
        var interval = new TimeSpan(1, 2, 3, 4).Add(TimeSpan.FromTicks(50));

        var withSignature = EncodeAndDecode(WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["When"] = moment }, Signature));
        var inferred = EncodeAndDecode(WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["When"] = moment, ["Length"] = interval }));

        Assert.Equal("20261104102030.123456-300", withSignature.FindProperty("When")!.DefaultValue);
        Assert.Equal("20261104102030.123456-300", inferred.FindProperty("When")!.DefaultValue);
        Assert.Equal("00000001020304.000005:000", inferred.FindProperty("Length")!.DefaultValue);
    }

    [Fact]
    public void A_date_array_is_converted_element_by_element()
    {
        var moment = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var block = WmiConnection.BuildParametersBlock(new Dictionary<string, object?> { ["Dates"] = new[] { moment } });

        Assert.Equal(new object?[] { "20260102030405.000000+000" }, (object?[])block.Class!.FindProperty("Dates")!.DefaultValue!);
    }

    [Fact]
    public void A_negative_interval_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DmtfDateTime.Format(TimeSpan.FromSeconds(-1)));
}
