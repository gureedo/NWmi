using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;

namespace NWmi.Tests.Dcom;

public class RemoteCreateInstanceReplyTests
{
    private static readonly Guid Login = Guid.Parse("f309ad18-d86a-11d0-a075-00c04fb68820");
    private static readonly Guid Ipid = Guid.Parse("12345678-9abc-def0-1122-334455667788");

    internal static byte[] StandardObjRefBytes() => new StandardObjRef(
        Login,
        new StdObjRef(0, 5, 0x1122334455667788, 0x0102030405060708, Ipid),
        new DualStringArray([new StringBinding(7, "10.0.0.5")], [])).ToBytes();

    /// <summary>A reply as a server would send it: ORPCTHAT, the properties as an interface pointer, then the HRESULT.</summary>
    internal static byte[] Reply(Guid outClsid, uint hresult = 0)
    {
        var scmReply = new ScmReplyInfoData(
            0x1122334455667788,
            new DualStringArray([new StringBinding(7, "10.0.0.5[49668]")], []),
            Ipid,
            RpcAuthLevel.Connect,
            new Version(5, 7));
        var interfaces = new PropsOutInfo([new InterfaceResult(Login, 0, StandardObjRefBytes())]);

        var blob = ActivationPropertiesBlob.Build(
        [
            scmReply,
            interfaces,
        ]);
        var objRef = new CustomObjRef(DcomIds.IActivationPropertiesOut, outClsid, blob).ToBytes();

        var writer = new NdrWriter();
        writer.WriteUInt32(0); // ORPCTHAT flags
        writer.WriteUniquePointer(false, null); // ORPCTHAT extensions
        writer.WriteMInterfacePointer(objRef);
        writer.WriteUInt32(hresult);
        return writer.ToArray();
    }

    [Fact]
    public void A_successful_reply_gives_the_exporter_and_the_interfaces()
    {
        var result = RemoteScmActivator.ParseRemoteCreateInstanceReply(Reply(DcomIds.ActivationPropertiesOut));

        Assert.Equal(0x1122334455667788UL, result.Reply.Oxid);
        Assert.Equal("10.0.0.5[49668]", Assert.Single(result.Reply.Bindings.StringBindings).NetworkAddress);
        Assert.Equal(Ipid, result.Reply.RemUnknownIpid);

        var objRef = result.GetStandardObjRef(0);
        Assert.Equal(Login, objRef.Iid);
        Assert.Equal(Ipid, objRef.Std.Ipid);
        Assert.Equal(5u, objRef.Std.PublicRefs);
    }

    [Fact]
    public void A_failure_status_is_raised_before_the_rest_is_read()
    {
        var failed = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02, 0x40, 0x00, 0x80 }; // E_NOINTERFACE
        var ex = Assert.Throws<DcomException>(() => RemoteScmActivator.ParseRemoteCreateInstanceReply(failed));
        Assert.Equal(unchecked((int)0x80004002u), ex.HResult);
    }

    [Fact]
    public void A_reply_that_is_not_ActivationPropertiesOut_is_rejected()
    {
        Assert.Throws<NdrFormatException>(() =>
            RemoteScmActivator.ParseRemoteCreateInstanceReply(Reply(Guid.NewGuid())));
    }

    [Fact]
    public void A_reply_without_properties_or_too_short_is_rejected()
    {
        Assert.Throws<NdrFormatException>(() => RemoteScmActivator.ParseRemoteCreateInstanceReply([1, 2]));

        var nothing = new byte[16]; // ORPCTHAT, a null properties pointer, S_OK
        Assert.Throws<NdrFormatException>(() => RemoteScmActivator.ParseRemoteCreateInstanceReply(nothing));
    }

    [Fact]
    public void Corrupt_replies_only_ever_throw_NdrFormatException_or_DcomException()
    {
        var original = Reply(DcomIds.ActivationPropertiesOut);
        var random = new Random(31);
        for (var round = 0; round < 3000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                RemoteScmActivator.ParseRemoteCreateInstanceReply(mutated);
            }
            catch (Exception ex) when (ex is NdrFormatException or DcomException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }
}
