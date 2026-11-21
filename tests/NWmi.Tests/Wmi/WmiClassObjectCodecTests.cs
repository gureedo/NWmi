using NWmi.Ndr;
using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Wmi;

public class WmiClassObjectCodecTests
{
    private static WmioClass SimpleClass() => new(
        "TestClass",
        [],
        [],
        [new WmioProperty("Name", CimType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)],
        []);

    private static WmioObjectBlock SampleInstance() =>
        WmioObjectBlock.ForInstance(WmioInstance.Create(SimpleClass(), new Dictionary<string, object?> { ["Name"] = "hello" }));

    [Fact]
    public void An_instance_round_trips_through_the_wire_representation()
    {
        var bytes = WmiClassObjectCodec.ToObjRefBytes(SampleInstance());
        var decoded = WmiClassObjectCodec.Decode(bytes);

        Assert.Equal("TestClass", decoded.Instance!.Class.Name);
        Assert.Equal("hello", decoded.Instance.Values["Name"]);
    }

    [Fact]
    public void WriteParameter_and_ReadParameter_round_trip_at_the_top_level()
    {
        var writer = new NdrWriter();
        WmiClassObjectCodec.WriteParameter(writer, SampleInstance());

        var reader = new NdrReader(writer.ToArray());
        var decoded = WmiClassObjectCodec.ReadParameter(reader);

        Assert.Equal("hello", decoded!.Instance!.Values["Name"]);
    }

    [Fact]
    public void WriteParameter_writes_a_null_pointer_for_a_null_value()
    {
        var writer = new NdrWriter();
        WmiClassObjectCodec.WriteParameter(writer, null);

        var reader = new NdrReader(writer.ToArray());
        Assert.Null(WmiClassObjectCodec.ReadParameter(reader));
    }

    [Fact]
    public void Decode_rejects_an_object_reference_with_the_wrong_unmarshaler()
    {
        var wrongClsid = NWmi.Dcom.DcomIds.ActivationPropertiesIn;
        var objRef = new NWmi.Dcom.CustomObjRef(WmiIds.IidWbemClassObject, wrongClsid, [1, 2, 3]).ToBytes();
        Assert.Throws<FormatException>(() => WmiClassObjectCodec.Decode(objRef));
    }
}
