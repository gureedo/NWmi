using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class DualStringArrayTests
{
    [Fact]
    public void The_smallest_array_matches_the_specification()
    {
        // MS-DCOM 2.2.19.1: wNumEntries = 4, wSecurityOffset = 2, then four zero entries.
        var empty = new DualStringArray([], []);
        Assert.Equal(Hex.Parse("0400 0200 0000 0000 0000 0000"), empty.ToPacked());

        var parsed = DualStringArray.ParsePacked(empty.ToPacked(), out var consumed);
        Assert.Empty(parsed.StringBindings);
        Assert.Empty(parsed.SecurityBindings);
        Assert.Equal(12, consumed);
    }

    [Fact]
    public void A_tcp_binding_and_a_security_binding_have_the_documented_layout()
    {
        var array = new DualStringArray(
            [new StringBinding(DualStringArray.TcpTowerId, "1.2.3.4")],
            [new SecurityBinding(RpcAuthType.WinNt, "")]);

        // 10 entries of string bindings (tower, 7 characters, terminator, nullterm1), then 4 of security bindings.
        var expected = Hex.Parse(
            "0e00 0a00 | 0700 3100 2e00 3200 2e00 3300 2e00 3400 0000 0000 | 0a00 ffff 0000 0000");
        Assert.Equal(expected, array.ToPacked());

        var parsed = DualStringArray.ParsePacked(expected, out var consumed);
        Assert.Equal(expected.Length, consumed);
        var binding = Assert.Single(parsed.StringBindings);
        Assert.Equal(DualStringArray.TcpTowerId, binding.TowerId);
        Assert.Equal("1.2.3.4", binding.NetworkAddress);
        var security = Assert.Single(parsed.SecurityBindings);
        Assert.Equal(RpcAuthType.WinNt, security.AuthnService);
        Assert.Equal("", security.PrincipalName);
    }

    [Fact]
    public void Several_bindings_keep_their_order_and_principal_names()
    {
        var array = new DualStringArray(
            [
                new StringBinding(7, "10.0.0.5[49668]"),
                new StringBinding(7, "fe80::1[49668]"),
                new StringBinding(0x0F, "SERVER[\\pipe\\epmapper]"),
            ],
            [new SecurityBinding(RpcAuthType.GssNegotiate, "Server$@CORP.EXAMPLE"), new SecurityBinding(RpcAuthType.WinNt, "")]);

        var parsed = DualStringArray.ParsePacked(array.ToPacked(), out _);

        Assert.Equal(array.StringBindings, parsed.StringBindings);
        Assert.Equal(array.SecurityBindings, parsed.SecurityBindings);
    }

    [Fact]
    public void Host_and_endpoint_are_split_at_the_brackets()
    {
        var withEndpoint = new StringBinding(7, "10.0.0.5[49668]");
        Assert.Equal("10.0.0.5", withEndpoint.Host);
        Assert.Equal("49668", withEndpoint.Endpoint);

        var withoutEndpoint = new StringBinding(7, "SERVER");
        Assert.Equal("SERVER", withoutEndpoint.Host);
        Assert.Null(withoutEndpoint.Endpoint);
    }

    [Fact]
    public void Trailing_bytes_after_the_array_are_not_consumed()
    {
        var packed = new DualStringArray([new StringBinding(7, "a")], []).ToPacked();
        var withExtra = packed.Concat(new byte[] { 0xAA, 0xBB }).ToArray();
        DualStringArray.ParsePacked(withExtra, out var consumed);
        Assert.Equal(packed.Length, consumed);
    }

    [Theory]
    [InlineData("0200")] // header cut short
    [InlineData("0400 0200 0000")] // fewer entries than announced
    [InlineData("0200 0200 0700 3100")] // string without terminator
    [InlineData("0400 0900 0000 0000 0000 0000")] // security offset outside the array
    [InlineData("0200 0100 0000 0a00")] // security binding cut short
    public void Malformed_arrays_are_rejected(string hex)
    {
        Assert.Throws<FormatException>(() => DualStringArray.ParsePacked(Hex.Parse(hex), out _));
    }

    [Fact]
    public void Corrupt_arrays_only_ever_throw_FormatException()
    {
        var original = new DualStringArray(
            [new StringBinding(7, "10.0.0.5[49668]"), new StringBinding(7, "SERVER")],
            [new SecurityBinding(RpcAuthType.WinNt, "x")]).ToPacked();

        var random = new Random(99);
        for (var round = 0; round < 4000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            try
            {
                DualStringArray.ParsePacked(mutated, out _);
            }
            catch (FormatException)
            {
                // Expected for corrupt input; any other exception type fails the test.
            }
        }
    }
}
