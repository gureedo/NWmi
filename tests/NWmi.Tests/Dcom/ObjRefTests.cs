using NWmi.Dcom;
using NWmi.Rpc;
using NWmi.Tests.Ndr;

namespace NWmi.Tests.Dcom;

public class ObjRefTests
{
    private static readonly Guid IUnknown = Guid.Parse("00000143-0000-0000-c000-000000000046");
    private static readonly Guid Ipid = Guid.Parse("12345678-9abc-def0-1122-334455667788");

    private static DualStringArray OneTcpBinding() =>
        new([new StringBinding(DualStringArray.TcpTowerId, "1.2.3.4")], []);

    [Fact]
    public void Standard_objref_has_the_documented_byte_layout()
    {
        var objRef = new StandardObjRef(
            IUnknown,
            new StdObjRef(Flags: 0, PublicRefs: 5, Oxid: 0x1122334455667788, Oid: 0x0102030405060708, Ipid),
            OneTcpBinding());

        var expected = Hex.Parse(
            "4d454f57 | 01000000 | " + // "MEOW" signature, OBJREF_STANDARD
            "43010000 0000 0000 c000 000000000046 | " + // iid
            "00000000 | 05000000 | 8877665544332211 | 0807060504030201 | " + // flags, public refs, oxid, oid
            "78563412 bc9a f0de 1122334455667788 | " + // ipid
            "0c00 0a00 | 0700 3100 2e00 3200 2e00 3300 2e00 3400 0000 0000 | 0000 0000"); // string array

        Assert.Equal(expected, objRef.ToBytes());
    }

    [Fact]
    public void Standard_objref_round_trips()
    {
        var original = new StandardObjRef(
            IUnknown,
            new StdObjRef(StdObjRef.NoPing, 5, 0xAABBCCDDEEFF0011, 0x2233, Ipid),
            new DualStringArray([new StringBinding(7, "SERVER[135]")], [new SecurityBinding(RpcAuthType.WinNt, "")]));

        var parsed = Assert.IsType<StandardObjRef>(ObjRef.Parse(original.ToBytes()));

        Assert.Equal(original.Iid, parsed.Iid);
        Assert.Equal(original.Std, parsed.Std);
        Assert.Equal(original.ResolverAddress.StringBindings, parsed.ResolverAddress.StringBindings);
        Assert.Equal(original.ResolverAddress.SecurityBindings, parsed.ResolverAddress.SecurityBindings);
    }

    [Fact]
    public void Handler_objref_round_trips()
    {
        var clsid = Guid.NewGuid();
        var original = new HandlerObjRef(IUnknown, new StdObjRef(0, 1, 2, 3, Ipid), clsid, OneTcpBinding());

        var parsed = Assert.IsType<HandlerObjRef>(ObjRef.Parse(original.ToBytes()));

        Assert.Equal(clsid, parsed.HandlerClsid);
        Assert.Equal(original.Std, parsed.Std);
        Assert.Equal("1.2.3.4", Assert.Single(parsed.ResolverAddress.StringBindings).NetworkAddress);
    }

    [Fact]
    public void Custom_objref_carries_opaque_data_after_a_24_byte_header()
    {
        var clsid = Guid.Parse("4590f812-1d3a-11d0-891f-00aa004b2e24"); // the WMI object marshaler (MS-WMI 2.2.4)
        var original = new CustomObjRef(Guid.Parse("dc12a681-737f-11cf-884d-00aa004b2e24"), clsid, [1, 2, 3, 4, 5]);

        var bytes = original.ToBytes();
        Assert.Equal(24 + 24 + 5, bytes.Length);
        Assert.Equal(Hex.Parse("04000000"), bytes[4..8]); // OBJREF_CUSTOM
        Assert.Equal(Hex.Parse("0102030405"), bytes[^5..]);

        var parsed = Assert.IsType<CustomObjRef>(ObjRef.Parse(bytes));
        Assert.Equal(clsid, parsed.Clsid);
        Assert.Equal(original.Iid, parsed.Iid);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, parsed.ObjectData);
    }

    [Fact]
    public void A_custom_objref_may_have_no_data()
    {
        var parsed = Assert.IsType<CustomObjRef>(ObjRef.Parse(new CustomObjRef(IUnknown, IUnknown, []).ToBytes()));
        Assert.Empty(parsed.ObjectData);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4d454f57")] // header cut short
    [InlineData("4d454f58 01000000 00000000000000000000000000000000")] // wrong signature
    [InlineData("4d454f57 08000000 00000000000000000000000000000000")] // OBJREF_EXTENDED is not supported
    [InlineData("4d454f57 01000000 00000000000000000000000000000000 00000000")] // standard form without a STDOBJREF
    [InlineData("4d454f57 04000000 00000000000000000000000000000000 0000")] // custom form without its header
    public void Malformed_or_unsupported_references_are_rejected(string hex)
    {
        Assert.Throws<FormatException>(() => ObjRef.Parse(Hex.Parse(hex)));
    }

    [Fact]
    public void Corrupt_references_only_ever_throw_FormatException()
    {
        var original = new StandardObjRef(IUnknown, new StdObjRef(0, 5, 1, 2, Ipid), OneTcpBinding()).ToBytes();
        var random = new Random(31337);
        for (var round = 0; round < 4000; round++)
        {
            var mutated = (byte[])original.Clone();
            for (var i = random.Next(1, 4); i > 0; i--)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            if (random.Next(4) == 0)
                mutated = mutated[..random.Next(mutated.Length)];

            try
            {
                ObjRef.Parse(mutated);
            }
            catch (FormatException)
            {
                // Expected; any other exception type fails the test.
            }
        }
    }
}
