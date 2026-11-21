using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Tests.Ndr;
using NWmi.Wmi;

namespace NWmi.Tests.Wmi;

public class WmiContextTests
{
    [Fact]
    public void Empty_context_buffer_has_no_properties()
    {
        var bytes = WmiContext.Empty.ToBufferBytes();
        Assert.Equal(Hex.Parse("01000000 | 00000000 00000000 00000000 00000000 | 00000000"), bytes);
    }

    [Fact]
    public void A_single_int32_property_matches_the_exact_byte_layout()
    {
        var context = WmiContext.Empty.SetInt32("A", 64);
        var bytes = context.ToBufferBytes();

        Assert.Equal(
            Hex.Parse(
                "01000000 | 00000000 00000000 00000000 00000000 | " + // NumGuids, the one GUID
                "01000000 | " + // NumProps
                "01000000 | 4100 | " + // PropertyName: StringLength=1, "A"
                "00000000 | " + // PropertyFlags
                "0300 | " + // PropertyType: VT_I4
                "4000000000000000"), // PropertyValue: 64 as a little-endian int32, in an 8-byte buffer
            bytes);
    }

    [Fact]
    public void SetX_replaces_an_earlier_property_of_the_same_name()
    {
        var context = WmiContext.Empty.SetInt32("A", 1).SetInt32("A", 2);
        Assert.Equal(WmiContext.Empty.SetInt32("A", 2).ToBufferBytes(), context.ToBufferBytes());
    }

    [Fact]
    public void WriteParameter_writes_a_null_pointer_for_an_empty_context()
    {
        var writer = new NdrWriter();
        WmiContext.WriteParameter(writer, null);
        Assert.Equal(Hex.Parse("00000000"), writer.ToArray());

        var writer2 = new NdrWriter();
        WmiContext.WriteParameter(writer2, WmiContext.Empty);
        Assert.Equal(Hex.Parse("00000000"), writer2.ToArray());
    }

    [Fact]
    public void WriteParameter_marshals_a_nonempty_context_as_a_custom_object_reference()
    {
        var context = WmiContext.Empty.SetBoolean("__RequiredArchitecture", true).SetString("__MI_DESTINATIONOPTIONS_DATA_LOCALE", "MS_409");

        var writer = new NdrWriter();
        WmiContext.WriteParameter(writer, context);

        var reader = new NdrReader(writer.ToArray());
        var pointer = reader.ReadMInterfacePointer();
        Assert.False(pointer.IsNull);

        var objRef = Assert.IsType<CustomObjRef>(ObjRef.Parse(pointer.Value!));
        Assert.Equal(WmiIds.IidWbemContext, objRef.Iid);
        Assert.Equal(WmiIds.ClsidWbemContext, objRef.Clsid);
        Assert.Equal(context.ToBufferBytes(), objRef.ObjectData);
    }
}
