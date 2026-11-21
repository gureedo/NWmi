using System.Buffers.Binary;
using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;
using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Wmi;

public class WbemWCOSmartEnumClientTests
{
    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly StdObjRef SmartEnumStd = new(0, 5, 0x1234, 0x5678, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static WmioClass OneIntProperty() => new(
        "TestClass", [], [],
        [new WmioProperty("Count", CimType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(false, false), 0, 0)], []);

    private static void WriteU32(MemoryStream stream, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static byte[] BuildDataPacketObject(byte objectType, Guid? classId, byte[] objectData)
    {
        byte[] inner;
        if (classId is null)
        {
            inner = new byte[8 + objectData.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(inner, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(4), (uint)objectData.Length);
            objectData.CopyTo(inner.AsSpan(8));
        }
        else
        {
            inner = new byte[0x18 + objectData.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(inner, 0x18);
            BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(4), (uint)objectData.Length);
            classId.Value.TryWriteBytes(inner.AsSpan(8, 16));
            objectData.CopyTo(inner.AsSpan(0x18));
        }

        using var stream = new MemoryStream();
        WriteU32(stream, 9);
        WriteU32(stream, (uint)inner.Length);
        stream.WriteByte(objectType);
        stream.Write(inner);
        return stream.ToArray();
    }

    private static byte[] BuildObjectArray(params byte[][] dataPacketObjects)
    {
        using var stream = new MemoryStream();
        var body = dataPacketObjects.SelectMany(b => b).ToArray();
        WriteU32(stream, 0);
        stream.Write("WBEMDATA"u8);
        WriteU32(stream, 0x1A);
        WriteU32(stream, 8);
        WriteU32(stream, 0);
        stream.WriteByte(1);
        stream.WriteByte(1);
        WriteU32(stream, 8);
        WriteU32(stream, (uint)(4 + body.Length));
        WriteU32(stream, 12);
        WriteU32(stream, (uint)body.Length);
        WriteU32(stream, (uint)dataPacketObjects.Length);
        stream.Write(body);
        return stream.ToArray();
    }

    private static byte[] EncodeFullInstanceObjectData(WmioInstance instance)
    {
        var writer = new WmioWriter();
        WmioObjectBlock.ForInstance(instance).Encode(writer);
        return writer.ToArray();
    }

    private static byte[] EncodeNoClassObjectData(WmioInstance instance)
    {
        var classWriter = new WmioWriter();
        instance.Class.Write(classWriter, hasMethodsPart: false);
        var classBytes = classWriter.ToArray();

        var fullWriter = new WmioWriter();
        instance.Write(fullWriter);
        var body = fullWriter.ToArray()[classBytes.Length..];

        var payload = new WmioWriter();
        payload.WriteByte(0x02); // ObjectFlags: instance, no decoration
        payload.WriteBytes(body);
        return payload.ToArray();
    }

    [Fact]
    public async Task Next_caches_a_classs_definition_across_calls_so_a_later_classless_instance_decodes()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.Start();
        await using (server)
        {
            var rpcClient = new RpcClient(clientSide);
            var exporterConnection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: TestContext.Current.CancellationToken);
            var connection = await WmiOrpcConnection.FromExporterConnectionAsync(
                exporterConnection, ownsConnection: true, SmartEnumStd, WmiIds.IidWbemWCOSmartEnum, security: null, cancellationToken: TestContext.Current.CancellationToken);
            var client = new WbemWcoSmartEnumClient(connection);

            var @class = OneIntProperty();
            var classId = Guid.NewGuid();
            var instance1 = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Count"] = 1 });
            var instance2 = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Count"] = 2 });

            Guid? sentProxyGuid = null;
            server.CallHandler = fragments =>
            {
                Assert.Equal((ushort)3, fragments[0].OpNum); // IWbemWCOSmartEnum::Next

                var stub = FakeRpcServer.Join(fragments);
                var reader = new NdrReader(stub.AsMemory()[32..]); // skip ORPCTHIS
                var proxyGuid = reader.ReadGuid();
                sentProxyGuid ??= proxyGuid;
                Assert.Equal(sentProxyGuid, proxyGuid); // the same session identifier on every call

                var isFirstCall = fragments[0].CallId == server.Requests[0].CallId;
                var buffer = isFirstCall
                    ? BuildObjectArray(BuildDataPacketObject(2, classId, EncodeFullInstanceObjectData(instance1)))
                    : BuildObjectArray(BuildDataPacketObject(3, classId, EncodeNoClassObjectData(instance2)));

                var reply = new NdrWriter();
                reply.WriteUInt32(0); // ORPCTHAT flags
                reply.WriteUInt32(0); // ORPCTHAT null extensions
                reply.WriteUInt32(1); // puReturned
                reply.WriteUInt32((uint)buffer.Length); // pdwBuffSize
                reply.WriteUInt32(0x20000); // pBuffer's inner pointer referent id (byte**: one level deeper than the array itself)
                reply.WriteMaxCount(buffer.Length); // pBuffer's own array bound
                reply.WriteBytes(buffer);
                reply.WriteInt32(0); // WBEM_S_NO_ERROR

                return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
            };

            var first = await client.NextAsync(1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, first.Objects.Single()!.Instance!.Values["Count"]);

            var second = await client.NextAsync(1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, second.Objects.Single()!.Instance!.Values["Count"]);
            Assert.Equal("TestClass", second.Objects.Single()!.Instance!.Class.Name);
        }
    }
}
