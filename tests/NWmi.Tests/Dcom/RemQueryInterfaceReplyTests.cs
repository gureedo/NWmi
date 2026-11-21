using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;

namespace NWmi.Tests.Dcom;

public class RemQueryInterfaceReplyTests
{
    private const uint ENoInterface = 0x80004002;
    private const uint EAccessDenied = 0x80070005;
    private const uint SFalse = 1;

    private static readonly Guid Iid = Guid.Parse("1c1c45ee-4395-11d2-b60b-00104b703efd");

    private static byte[] StatusOnly(uint status)
    {
        var writer = new NdrWriter();
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(status);
        return writer.ToArray();
    }

    [Fact]
    public void E_NOINTERFACE_for_the_whole_call_reports_each_interface_as_unsupported()
    {
        var result = Assert.Single(RemUnknown2.ParseQueryInterfaceReply(StatusOnly(ENoInterface), [Iid]));

        Assert.False(result.Succeeded);
        Assert.Null(result.ObjRef);
    }

    [Fact]
    public void S_FALSE_is_a_partial_result_read_from_the_per_interface_statuses()
    {
        var writer = new NdrWriter();
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteMaxCount(1);
        writer.WriteUInt32(ENoInterface);
        writer.WriteMaxCount(1);
        writer.WriteMInterfacePointer(null);
        writer.WriteUInt32(SFalse);

        var result = Assert.Single(RemUnknown2.ParseQueryInterfaceReply(writer.ToArray(), [Iid]));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Any_other_failure_is_still_an_error() =>
        Assert.Throws<DcomException>(() => RemUnknown2.ParseQueryInterfaceReply(StatusOnly(EAccessDenied), [Iid]));
}
