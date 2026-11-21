using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class RemUnknownTypesTests
{
    private static readonly Guid Ipid = Guid.Parse("12345678-9abc-def0-1122-334455667788");

    [Fact]
    public void An_interface_ref_is_a_guid_and_two_counts()
    {
        var writer = new NdrWriter();
        new RemInterfaceRef(Ipid, 5, 2).WriteNdr(writer);

        Assert.Equal(Hex.Parse("78563412 bc9a f0de 1122334455667788 | 05000000 | 02000000"), writer.ToArray());
    }

    [Fact]
    public void A_query_result_pads_its_std_to_eight_bytes()
    {
        var result = new RemQiResult(0, new StdObjRef(0, 5, 0x1122334455667788, 0x0102030405060708, Ipid));
        var writer = new NdrWriter();
        result.WriteNdr(writer);

        // HRESULT, 4 bytes of padding, then flags, public refs, oxid, oid, ipid: 48 bytes in all.
        Assert.Equal(
            Hex.Parse(
                "00000000 | 00000000 | 00000000 | 05000000 | 8877665544332211 | 0807060504030201 | " +
                "78563412 bc9a f0de 1122334455667788"),
            writer.ToArray());
    }

    [Fact]
    public void A_query_result_round_trips()
    {
        var original = new RemQiResult(0, new StdObjRef(StdObjRef.NoPing, 7, 11, 22, Ipid));
        var writer = new NdrWriter();
        original.WriteNdr(writer);

        var reader = new NdrReader(writer.ToArray());
        var read = RemQiResult.ReadNdr(reader);

        Assert.Equal(original, read);
        Assert.True(read.Succeeded);
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void A_failed_result_is_not_succeeded()
    {
        Assert.False(new RemQiResult(unchecked((int)0x80004002), default).Succeeded);
    }

    [Fact]
    public void A_truncated_result_is_rejected()
    {
        Assert.Throws<NdrFormatException>(() => RemQiResult.ReadNdr(new NdrReader(new byte[20])));
    }
}
