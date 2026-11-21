using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Rpc;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class ServerAlive2Tests
{
    // COMVERSION 5.7, a referent id, conformance 14, wNumEntries 14, wSecurityOffset 10, the 14 entries
    // (tcp "1.2.3.4" with terminators, then security binding 10 with an empty name), pReserved, status 0.
    private const string Reply =
        "0500 0700 | 00000200 | 0e000000 | 0e00 0a00 | " +
        "0700 3100 2e00 3200 2e00 3300 2e00 3400 0000 0000 | 0a00 ffff 0000 0000 | 00000000 | 00000000";

    [Fact]
    public void The_reply_is_decoded()
    {
        var result = ObjectExporter.ParseServerAlive2Reply(Hex.Parse(Reply));

        Assert.Equal(new Version(5, 7), result.Version);
        var binding = Assert.Single(result.Bindings.StringBindings);
        Assert.Equal(DualStringArray.TcpTowerId, binding.TowerId);
        Assert.Equal("1.2.3.4", binding.NetworkAddress);
        Assert.Equal(RpcAuthType.WinNt, Assert.Single(result.Bindings.SecurityBindings).AuthnService);
    }

    [Fact]
    public void A_failure_status_is_raised_before_anything_else_is_read()
    {
        // The out parameters are empty (null pointer) and the status is E_ACCESSDENIED.
        var failed = Hex.Parse("0000 0000 | 00000000 | 00000000 | 05000780");

        var ex = Assert.Throws<DcomException>(() => ObjectExporter.ParseServerAlive2Reply(failed));
        Assert.Equal(unchecked((int)0x80070005u), ex.HResult);
    }

    [Fact]
    public void A_reply_too_short_for_a_status_is_rejected()
    {
        Assert.Throws<NdrFormatException>(() => ObjectExporter.ParseServerAlive2Reply([1, 2, 3]));
    }
}
