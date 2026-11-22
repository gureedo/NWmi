using System.Xml.Linq;
using NWmi.Client;
using NWmi.WinRm;
using NWmi.Wmio;

namespace NWmi.Tests.Client;

public class WmiTypeTests
{
    public static TheoryData<ushort, WmiType> WireTypes() => new()
    {
        { (ushort)WbemType.Boolean, WmiType.Boolean },
        { (ushort)WbemType.Uint8, WmiType.UInt8 },
        { (ushort)WbemType.Sint8, WmiType.SInt8 },
        { (ushort)WbemType.Uint16, WmiType.UInt16 },
        { (ushort)WbemType.Sint16, WmiType.SInt16 },
        { (ushort)WbemType.Uint32, WmiType.UInt32 },
        { (ushort)WbemType.Sint32, WmiType.SInt32 },
        { (ushort)WbemType.Uint64, WmiType.UInt64 },
        { (ushort)WbemType.Sint64, WmiType.SInt64 },
        { (ushort)WbemType.Real32, WmiType.Real32 },
        { (ushort)WbemType.Real64, WmiType.Real64 },
        { (ushort)WbemType.Char16, WmiType.Char16 },
        { (ushort)WbemType.DateTime, WmiType.DateTime },
        { (ushort)WbemType.String, WmiType.String },
        { (ushort)WbemType.Reference, WmiType.Reference },
        { (ushort)WbemType.Object, WmiType.Instance },
    };

    [Theory]
    [MemberData(nameof(WireTypes))]
    public void Every_wire_type_maps_both_ways_as_a_scalar_and_as_an_array(ushort raw, WmiType type)
    {
        var wire = (WbemType)raw;
        Assert.Equal(type, WmiTypeMapping.ToWmiType(wire));
        Assert.Equal(wire, WmiTypeMapping.ToWbemType(type));

        var arrayType = WmiTypeMapping.ToWmiType(wire.AsArray());
        Assert.Equal(type + 16, arrayType);
        Assert.EndsWith("Array", arrayType.ToString(), StringComparison.Ordinal);
        Assert.Equal(wire.AsArray(), WmiTypeMapping.ToWbemType(arrayType));
    }

    [Fact]
    public void Unknown_has_no_wire_type_and_no_array_type()
    {
        Assert.Throws<ArgumentException>(() => WmiTypeMapping.ToWbemType(WmiType.Unknown));
        Assert.Throws<ArgumentException>(() => WmiTypeMapping.AsArray(WmiType.Unknown));
        Assert.Throws<ArgumentException>(() => WmiTypeMapping.AsArray(WmiType.StringArray));
    }

    [Theory]
    [InlineData("uint32", false, WmiType.UInt32)]
    [InlineData("datetime", true, WmiType.DateTimeArray)]
    [InlineData("reference", false, WmiType.Reference)]
    [InlineData("char16", false, WmiType.Char16)]
    [InlineData("no-such-type", true, WmiType.Unknown)]
    public void A_CIM_XML_type_name_maps_to_its_type(string name, bool isArray, WmiType expected) =>
        Assert.Equal(expected, WmiTypeMapping.FromCimXml(name, isArray));

    [Fact]
    public void Qualifier_flavors_map_to_override_and_propagation_flags()
    {
        // As MMI reports Win32_Process's qualifiers: key is [DisableOverride, ToSubclass], read is [EnableOverride, ToSubclass],
        // the class's CreateBy is [EnableOverride, Restricted].
        Assert.Equal(WmiFlags.DisableOverride | WmiFlags.ToSubclass,
            WmiTypeMapping.FlavorFlags(QualifierFlavor.NotOverridable | QualifierFlavor.PropagateToDerivedClass |
                                       QualifierFlavor.PropagateToInstance));
        Assert.Equal(WmiFlags.EnableOverride | WmiFlags.ToSubclass,
            WmiTypeMapping.FlavorFlags(QualifierFlavor.PropagateToDerivedClass));
        Assert.Equal(WmiFlags.EnableOverride | WmiFlags.Restricted,
            WmiTypeMapping.FlavorFlags(QualifierFlavor.PropagateToInstance));
        Assert.Equal(WmiFlags.EnableOverride | WmiFlags.Restricted | WmiFlags.Translatable,
            WmiTypeMapping.FlavorFlags(QualifierFlavor.Amended));
    }

    private static WmioQualifier Flag(string name, bool value = true) =>
        new(name, QualifierFlavor.PropagateToDerivedClass, WbemType.Boolean, value);

    [Fact]
    public void Declaration_flags_come_from_the_qualifiers()
    {
        Assert.Equal(WmiFlags.Property, WmiTypeMapping.DeclarationFlags([], false));
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly,
            WmiTypeMapping.DeclarationFlags([Flag("key"), Flag("read")], false));
        // Win32_Environment.Name: read and write, so not read-only.
        Assert.Equal(WmiFlags.Property | WmiFlags.Key,
            WmiTypeMapping.DeclarationFlags([Flag("Key"), Flag("read"), Flag("write")], false));
        Assert.Equal(WmiFlags.Property | WmiFlags.Required,
            WmiTypeMapping.DeclarationFlags([Flag("Required")], false));
        Assert.Equal(WmiFlags.Property, WmiTypeMapping.DeclarationFlags([Flag("key", false)], false));
        Assert.Equal(WmiFlags.Parameter | WmiFlags.In | WmiFlags.Out,
            WmiTypeMapping.DeclarationFlags([Flag("In"), Flag("Out")], true));
        Assert.Equal(WmiFlags.Property, WmiTypeMapping.DeclarationFlags([Flag("In")], false));
    }

    private static WmioClass ProcessClass() => new(
        "Win32_Process",
        [],
        [],
        [
            new WmioProperty("Handle", WbemType.String, false, 0, 0, [Flag("key"), Flag("read")],
                new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("ProcessId", WbemType.Uint32, false, 1, 0, [Flag("read")],
                new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Priority", WbemType.Uint32, false, 2, 0, [Flag("read"), Flag("write")],
                new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Tags", WbemType.String.AsArray(), false, 3, 0, [],
                new NullAndDefaultFlag(true, false), null, 0),
        ],
        []);

    private static WmiObject Process()
    {
        var values = new Dictionary<string, object?> { ["Handle"] = "4", ["ProcessId"] = 4u, ["Priority"] = 8u, ["Tags"] = null };
        return new WmiObject(WmioObjectBlock.ForInstance(WmioInstance.Create(ProcessClass(), values)), null, null);
    }

    [Fact]
    public void An_instance_property_has_its_declared_type_and_flags()
    {
        var obj = Process();

        var handle = obj.GetProperty("handle");
        Assert.Equal("Handle", handle.Name);
        Assert.Equal("4", handle.Value);
        Assert.Equal(WmiType.String, handle.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NotModified, handle.Flags);

        Assert.Equal(WmiType.UInt32, obj.GetProperty("ProcessId").Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.NotModified, obj.GetProperty("Priority").Flags);

        var tags = obj.GetProperty("Tags");
        Assert.Equal(WmiType.StringArray, tags.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.NullValue | WmiFlags.NotModified, tags.Flags);
    }

    [Fact]
    public void Setting_a_value_clears_NotModified()
    {
        var obj = Process();

        obj["priority"] = 4u;
        obj["Tags"] = new[] { "a" };

        Assert.Equal(WmiFlags.Property, obj.GetProperty("Priority").Flags);
        Assert.Equal(WmiFlags.Property, obj.GetProperty("Tags").Flags);
        Assert.True(obj.GetProperty("Handle").Flags.HasFlag(WmiFlags.NotModified));
    }

    [Fact]
    public void GetProperties_lists_every_property_and_GetProperty_rejects_others()
    {
        var obj = Process();

        Assert.Equal(["Handle", "ProcessId", "Priority", "Tags"], obj.GetProperties().Select(p => p.Name));
        Assert.Throws<KeyNotFoundException>(() => obj.GetProperty("Missing"));
        Assert.Throws<KeyNotFoundException>(() => obj.GetProperty("__CLASS"));
    }

    [Fact]
    public void A_class_objects_properties_are_never_NotModified()
    {
        var template = new WmiObject(WmioObjectBlock.ForClass(ProcessClass()), null, null);

        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NullValue,
            template.GetProperty("Handle").Flags);
    }

    [Fact]
    public void Method_output_parameters_are_parameters()
    {
        var @class = new WmioClass("__PARAMETERS", [], [],
        [
            new WmioProperty("ReturnValue", WbemType.Uint32, false, 0, 0, [Flag("out")],
                new NullAndDefaultFlag(true, false), null, 0),
        ], []);
        var output = new WmiObject(
            WmioObjectBlock.ForInstance(WmioInstance.Create(@class, new Dictionary<string, object?> { ["ReturnValue"] = 0u })),
            null, null);

        Assert.Equal(WmiFlags.Parameter | WmiFlags.Out | WmiFlags.NotModified, output.GetProperty("ReturnValue").Flags);
    }

    [Fact]
    public void A_WinRM_object_is_typed_by_its_class_definition_and_keyed_by_its_selectors()
    {
        var schema = WsmanClassSchema.Parse(XElement.Parse("""
            <CLASS NAME="Win32_Thing">
              <PROPERTY NAME="Handle" TYPE="uint32"/>
              <PROPERTY.ARRAY NAME="Sizes" TYPE="uint16"/>
              <PROPERTY NAME="Startup" TYPE="string"/>
              <PROPERTY NAME="Gone" TYPE="string"/>
            </CLASS>
            """));
        var epr = XElement.Parse("""
            <a:EndpointReference xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd">
              <a:ReferenceParameters><w:SelectorSet><w:Selector Name="Handle">4</w:Selector></w:SelectorSet></a:ReferenceParameters>
            </a:EndpointReference>
            """);
        var instance = XElement.Parse("""
            <p:Win32_Thing xmlns:p="urn:x" xmlns:i="urn:y" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <p:Handle>4</p:Handle><p:Sizes>1</p:Sizes>
              <p:Startup xsi:type="i:Win32_ProcessStartup_Type"><i:ShowWindow>1</i:ShowWindow></p:Startup>
              <p:Gone xsi:nil="true"/><p:Extra>x</p:Extra>
            </p:Win32_Thing>
            """);

        var obj = WsmanObjectReader.Read(instance, epr, null, "h", "ns", null, name => name == "Win32_Thing" ? schema : null);

        var handle = obj.GetProperty("Handle");
        Assert.Equal(WmiType.UInt32, handle.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.NotModified, handle.Flags);
        Assert.Equal(WmiType.UInt16Array, obj.GetProperty("Sizes").Type);
        Assert.Equal(WmiType.Instance, obj.GetProperty("Startup").Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.NullValue | WmiFlags.NotModified, obj.GetProperty("Gone").Flags);
        Assert.Equal(WmiType.Unknown, obj.GetProperty("Extra").Type);
    }

    [Fact]
    public void A_WinRM_object_without_a_class_definition_is_untyped_except_for_embedded_objects()
    {
        var instance = XElement.Parse("""
            <p:Win32_Thing xmlns:p="urn:x" xmlns:i="urn:y" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <p:Handle>4</p:Handle>
              <p:Startup xsi:type="i:Win32_ProcessStartup_Type"><i:ShowWindow>1</i:ShowWindow></p:Startup>
            </p:Win32_Thing>
            """);

        var obj = WsmanObjectReader.Read(instance, null, ["Handle"], "h", "ns");

        Assert.Equal(WmiType.Unknown, obj.GetProperty("Handle").Type);
        Assert.True(obj.GetProperty("Handle").Flags.HasFlag(WmiFlags.Key));
        Assert.Equal(WmiType.Instance, obj.GetProperty("Startup").Type);
    }
}
