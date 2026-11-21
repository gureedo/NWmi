using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;

namespace NWmi.Tests.Dcom;

public class RemoteScmActivatorCallTests
{
    private static readonly Guid ClassId = Guid.Parse("8bc3f05e-d86b-11d0-a075-00c04fb68820");
    private static readonly Guid Login = Guid.Parse("f309ad18-d86a-11d0-a075-00c04fb68820");

    [Fact]
    public async Task CreateInstance_sends_opnum_4_and_returns_the_decoded_reply()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
        [
            new RpcResponsePdu
            {
                CallId = fragments[0].CallId,
                ContextId = fragments[0].ContextId,
                Stub = RemoteCreateInstanceReplyTests.Reply(DcomIds.ActivationPropertiesOut),
            },
        ];
        server.Start();
        await using var client = new RpcClient(clientSide);

        var context = (await client.BindAsync([DcomIds.RemoteScmActivator], cancellationToken: TestContext.Current.CancellationToken))[0];
        var activator = new RemoteScmActivator(client, context);

        var result = await activator.CreateInstanceAsync(ClassId, [Login], TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((ushort)4, request.OpNum);
        Assert.Null(request.ObjectUuid);

        var reader = new NdrReader(request.Stub.AsMemory()[32..]);
        reader.ReadUInt32();
        var custom = (CustomObjRef)ObjRef.Parse(reader.ReadMInterfacePointer().Value!);
        var instantiation = ActivationPropertiesBlob.Parse(custom.ObjectData).OfType<InstantiationInfoData>().Single();
        Assert.Equal(ClassId, instantiation.ClassId);

        Assert.Equal(0x1122334455667788UL, result.Reply.Oxid);
        Assert.Equal(Login, result.GetStandardObjRef(0).Iid);
    }

    [Fact]
    public async Task A_failure_from_the_server_becomes_a_DcomException()
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        await using var server = new FakeRpcServer(serverSide);
        server.CallHandler = fragments =>
        [
            new RpcResponsePdu
            {
                CallId = fragments[0].CallId,
                ContextId = fragments[0].ContextId,
                Stub = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x54, 0x01, 0x04, 0x80], // CO_E_CLASSSTRING-like failure
            },
        ];
        server.Start();
        await using var client = new RpcClient(clientSide);
        var context = (await client.BindAsync([DcomIds.RemoteScmActivator], cancellationToken: TestContext.Current.CancellationToken))[0];

        var ex = await Assert.ThrowsAsync<DcomException>(
            () => new RemoteScmActivator(client, context).CreateInstanceAsync(ClassId, [Login], TestContext.Current.CancellationToken));
        Assert.Equal(unchecked((int)0x80040154u), ex.HResult);
    }
}
