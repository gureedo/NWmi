using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Rpc;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class ScmReplyInfoLayoutTests
{
    private static readonly Guid Ipid = Guid.Parse("12345678-9abc-def0-1122-334455667788");

    private static ScmReplyInfoData Sample() => new(
        0x1122334455667788,
        new DualStringArray([new StringBinding(7, "a")], []),
        Ipid,
        RpcAuthLevel.Connect,
        new Version(5, 7));

    [Fact]
    public void The_encoding_has_the_documented_layout()
    {
        var expected = Hex.Parse(
            "00000000 | 00000200 | " +
            "8877665544332211 | 04000200 | " +
            "78563412 bc9a f0de 1122334455667788 | 02000000 | 0500 0700 | " +
            "06000000 | 0600 0400 | 0700 6100 0000 0000 0000 0000");

        Assert.Equal(expected, Sample().Encode());
    }

    [Fact]
    public void A_reply_round_trips()
    {
        var decoded = ScmReplyInfoData.Decode(Sample().Encode());

        Assert.Equal(0x1122334455667788UL, decoded.Oxid);
        Assert.Equal("a", Assert.Single(decoded.Bindings.StringBindings).NetworkAddress);
        Assert.Equal(Ipid, decoded.RemUnknownIpid);
        Assert.Equal(RpcAuthLevel.Connect, decoded.AuthnHint);
        Assert.Equal(new Version(5, 7), decoded.ServerVersion);
    }
}
