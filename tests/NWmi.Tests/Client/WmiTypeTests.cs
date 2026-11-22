using System.Xml.Linq;
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

    private static WmiInstance Process()
    {
        var values = new Dictionary<string, object?> { ["Handle"] = "4", ["ProcessId"] = 4u, ["Priority"] = 8u, ["Tags"] = null };
        return WmiInstance.FromWire(WmioObjectBlock.ForInstance(WmioInstance.Create(ProcessClass(), values)), null, null);
    }

    [Fact]
    public void An_instance_property_has_its_declared_type_and_flags()
    {
        var obj = Process();

        var handle = obj.Properties["handle"]!;
        Assert.Equal("Handle", handle.Name);
        Assert.Equal("4", handle.Value);
        Assert.Equal(WmiType.String, handle.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NotModified, handle.Flags);

        Assert.Equal(WmiType.UInt32, obj.Properties["ProcessId"]!.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.NotModified, obj.Properties["Priority"]!.Flags);

        var tags = obj.Properties["Tags"]!;
        Assert.Equal(WmiType.StringArray, tags.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.NullValue | WmiFlags.NotModified, tags.Flags);
    }

    [Fact]
    public void Setting_a_value_clears_NotModified_and_NullValue_follows_the_value()
    {
        var obj = Process();

        obj["priority"] = 4u;
        obj["Tags"] = new[] { "a" };

        Assert.Equal(WmiFlags.Property, obj.Properties["Priority"]!.Flags);
        Assert.Equal(WmiFlags.Property, obj.Properties["Tags"]!.Flags);
        Assert.True(obj.Properties["Handle"]!.Flags.HasFlag(WmiFlags.NotModified));

        obj["Tags"] = null;
        Assert.Equal(WmiFlags.Property | WmiFlags.NullValue, obj.Properties["Tags"]!.Flags);
    }

    [Fact]
    public void Properties_lists_every_property_and_not_the_system_ones()
    {
        var obj = Process();

        Assert.Equal(["Handle", "ProcessId", "Priority", "Tags"], obj.Properties.Select(p => p.Name));
        Assert.Null(obj.Properties["Missing"]);
        Assert.Null(obj.Properties["__CLASS"]);
    }

    [Fact]
    public void A_class_declares_its_properties_flags_and_never_NotModified()
    {
        var @class = WmiClass.FromWire(ProcessClass(), null, null);

        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NullValue,
            @class.Properties["Handle"]!.Flags);
        Assert.Equal(WmiType.StringArray, @class.Properties["tags"]!.Type);
    }

    [Fact]
    public void Method_output_parameters_are_parameters()
    {
        var @class = new WmioClass("__PARAMETERS", [], [],
        [
            new WmioProperty("ReturnValue", WbemType.Uint32, false, 0, 0, [Flag("out")],
                new NullAndDefaultFlag(true, false), null, 0),
        ], []);
        var output = WmiInstance.FromWire(
            WmioObjectBlock.ForInstance(WmioInstance.Create(@class, new Dictionary<string, object?> { ["ReturnValue"] = 0u })),
            null, null);

        Assert.Equal(WmiFlags.Parameter | WmiFlags.Out | WmiFlags.NotModified, output.Properties["ReturnValue"]!.Flags);
    }

    private static WmioQualifier Typed(string name, object value) =>
        new(name, QualifierFlavor.PropagateToDerivedClass, value is int ? WbemType.Sint32 : WbemType.String, value);

    private static byte[] Signature(params WmioProperty[] properties)
    {
        var writer = new WmioWriter();
        WmioObjectBlock.ForClass(new WmioClass("__PARAMETERS", [], [], properties, [])).Encode(writer);
        return writer.ToArray();
    }

    private static WmioProperty Parameter(string name, WbemType type, int id, params WmioQualifier[] qualifiers) =>
        new(name, type, false, (ushort)id, 0, [Typed("ID", id), .. qualifiers], new NullAndDefaultFlag(true, false), null, 0);

    [Fact]
    public void A_DCOM_class_declares_its_methods_parameters_qualifiers_and_references()
    {
        var create = new WmioMethod("Create", false, 0,
            Signature(
                Parameter("CommandLine", WbemType.String, 0, Flag("In")),
                Parameter("ProcessStartupInformation", WbemType.Object, 2, Flag("In"), Typed("CIMTYPE", "object:Win32_ProcessStartup")),
                Parameter("CurrentDirectory", WbemType.String, 1, Flag("In"))),
            Signature(
                new WmioProperty("ReturnValue", WbemType.Uint32, false, 0, 0, [Flag("out"), Typed("CIMTYPE", "uint32")],
                    new NullAndDefaultFlag(true, false), null, 0),
                Parameter("ProcessId", WbemType.Uint32, 3, Flag("Out"))))
        {
            Qualifiers = [Flag("Static"), Flag("Implemented")],
        };
        var noArguments = new WmioMethod("Ping", false, 0, [], []);
        var wire = new WmioClass("Win32_Process", ["CIM_Process", "CIM_LogicalElement"],
            [Typed("provider", "CIMWin32"), Typed("CIMTYPE", "unused")],
            [
                new WmioProperty("Handle", WbemType.String, false, 0, 0, [Flag("key"), Flag("read"), Typed("CIMTYPE", "string")],
                    new NullAndDefaultFlag(true, false), null, 0),
                new WmioProperty("Owner", WbemType.Reference, false, 1, 0, [Typed("CIMTYPE", "ref:Win32_Account")],
                    new NullAndDefaultFlag(true, false), null, 0),
                new WmioProperty("Priority", WbemType.Uint32, false, 2, 0, [], new NullAndDefaultFlag(false, false), 8u, 0),
            ],
            [create, noArguments]);

        var @class = WmiClass.FromWire(wire, "server01", @"root\cimv2");

        Assert.Equal("Win32_Process", @class.Name);
        Assert.Equal("CIM_Process", @class.SuperClassName);
        Assert.Equal(@"\\server01\root\cimv2:Win32_Process", @class.SystemProperties.Path);
        Assert.Equal(["provider"], @class.Qualifiers.Select(q => q.Name)); // CIMTYPE is the type, not listed
        var handle = @class.Properties["Handle"]!;
        Assert.Equal(["key", "read"], handle.Qualifiers.Select(q => q.Name));
        Assert.Equal(WmiFlags.EnableOverride | WmiFlags.ToSubclass, handle.Qualifiers["key"]!.Flags);
        Assert.Equal("Win32_Account", @class.Properties["Owner"]!.ReferenceClassName);
        Assert.Null(handle.ReferenceClassName);
        Assert.Equal(8u, @class.Properties["Priority"]!.Value);
        Assert.Equal(WmiFlags.Property, @class.Properties["Priority"]!.Flags);

        var method = @class.Methods["create"]!;
        Assert.Equal(WmiType.UInt32, method.ReturnType);
        Assert.Equal(["Static", "Implemented"], method.Qualifiers.Select(q => q.Name));
        Assert.Equal(["CommandLine", "CurrentDirectory", "ProcessStartupInformation", "ProcessId"],
            method.Parameters.Select(p => p.Name)); // by ID, without ReturnValue
        Assert.Equal(WmiType.Instance, method.Parameters["ProcessStartupInformation"]!.Type);
        Assert.Equal(WmiFlags.Parameter | WmiFlags.In, method.Parameters["CommandLine"]!.Flags);
        Assert.Equal(WmiFlags.Parameter | WmiFlags.Out, method.Parameters["ProcessId"]!.Flags);
        Assert.Equal(["ID", "Out"], method.Parameters["ProcessId"]!.Qualifiers.Select(q => q.Name));

        var ping = @class.Methods["Ping"]!;
        Assert.Empty(ping.Parameters);
        Assert.Equal(WmiType.Unknown, ping.ReturnType);
    }

    [Fact]
    public void A_parameter_that_is_in_and_out_is_listed_once()
    {
        var method = new WmioMethod("Swap", false, 0,
            Signature(Parameter("Value", WbemType.Sint32, 0, Flag("In"))),
            Signature(Parameter("Value", WbemType.Sint32, 0, Flag("Out"))));
        var @class = WmiClass.FromWire(new WmioClass("NWmi_Swap", [], [], [], [method]), null, null);

        var parameter = Assert.Single(@class.Methods["Swap"]!.Parameters);
        Assert.Equal(WmiFlags.Parameter | WmiFlags.In | WmiFlags.Out, parameter.Flags);
    }

    [Fact]
    public void A_methods_qualifiers_are_not_sent_but_its_signatures_are()
    {
        var create = new WmioMethod("Create", false, 0, Signature(Parameter("A", WbemType.String, 0, Flag("In"))), [])
        {
            Qualifiers = [Flag("Static")],
        };
        var writer = new WmioWriter();
        WmioObjectBlock.ForClass(new WmioClass("NWmi_M", [], [], [], [create])).Encode(writer);

        // A client never sends a method's qualifiers; the methods themselves decode again.
        var decoded = WmioObjectBlock.Decode(new WmioReader(writer.ToArray())).Class!.Methods.Single();
        Assert.Empty(decoded.Qualifiers);
        Assert.Equal("A", decoded.DecodeInputSignature()!.Properties.Single().Name);
    }

    private const string ThingClass = """
        <CLASS NAME="Win32_Thing" SUPERCLASS="CIM_Thing" DERIVATION="CIM_Thing">
          <QUALIFIER NAME="dynamic" TYPE="boolean" TOSUBCLASS="false"><VALUE>TRUE</VALUE></QUALIFIER>
          <PROPERTY NAME="Handle" TYPE="uint32">
            <QUALIFIER NAME="key" TYPE="boolean" OVERRIDABLE="false" TOINSTANCE="true"><VALUE>TRUE</VALUE></QUALIFIER>
            <QUALIFIER NAME="read" TYPE="boolean"><VALUE>TRUE</VALUE></QUALIFIER>
          </PROPERTY>
          <PROPERTY.ARRAY NAME="Sizes" TYPE="uint16">
            <QUALIFIER NAME="ValueMap" TYPE="string"><VALUE.ARRAY><VALUE>1</VALUE><VALUE>2</VALUE></VALUE.ARRAY></QUALIFIER>
          </PROPERTY.ARRAY>
          <PROPERTY NAME="Startup" TYPE="string">
            <QUALIFIER NAME="EmbeddedInstance" TYPE="string"><VALUE>Win32_ProcessStartup</VALUE></QUALIFIER>
          </PROPERTY>
          <PROPERTY NAME="Gone" TYPE="string"><QUALIFIER NAME="read" TYPE="boolean"><VALUE>TRUE</VALUE></QUALIFIER><QUALIFIER NAME="write" TYPE="boolean"><VALUE>TRUE</VALUE></QUALIFIER></PROPERTY>
          <PROPERTY NAME="Level" TYPE="sint32"><VALUE>3</VALUE></PROPERTY>
          <PROPERTY.REFERENCE NAME="Owner" REFERENCECLASS="Win32_Account"></PROPERTY.REFERENCE>
          <METHOD NAME="Create" TYPE="uint32">
            <QUALIFIER NAME="Static" TYPE="boolean"><VALUE>TRUE</VALUE></QUALIFIER>
            <PARAMETER NAME="ProcessId" TYPE="uint32">
              <QUALIFIER NAME="ID" TYPE="sint32"><VALUE>1</VALUE></QUALIFIER>
              <QUALIFIER NAME="Out" TYPE="boolean"><VALUE>TRUE</VALUE></QUALIFIER>
            </PARAMETER>
            <PARAMETER NAME="CommandLine" TYPE="string">
              <QUALIFIER NAME="ID" TYPE="sint32"><VALUE>0</VALUE></QUALIFIER>
              <QUALIFIER NAME="IN" TYPE="boolean"><VALUE>TRUE</VALUE></QUALIFIER>
            </PARAMETER>
            <PARAMETER.ARRAY NAME="Data" TYPE="uint8">
              <QUALIFIER NAME="ID" TYPE="sint32"><VALUE>2</VALUE></QUALIFIER>
              <QUALIFIER NAME="out" TYPE="boolean"><VALUE>TRUE</VALUE></QUALIFIER>
            </PARAMETER.ARRAY>
          </METHOD>
        </CLASS>
        """;

    [Fact]
    public void A_WinRM_class_definition_is_read_with_its_qualifiers_methods_and_defaults()
    {
        var @class = WsmanClassSchema.Parse(XElement.Parse(ThingClass), "h", @"root\cimv2").Class;

        Assert.Equal("Win32_Thing", @class.Name);
        Assert.Equal("CIM_Thing", @class.SuperClassName);
        Assert.Equal(@"\\h\root\cimv2:Win32_Thing", @class.SystemProperties.Path);
        var dynamic = Assert.Single(@class.Qualifiers);
        Assert.Equal(true, dynamic.Value);
        Assert.Equal(WmiFlags.EnableOverride | WmiFlags.Restricted, dynamic.Flags);

        var handle = @class.Properties["handle"]!;
        Assert.Equal(WmiType.UInt32, handle.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NullValue, handle.Flags);
        Assert.Equal(WmiFlags.DisableOverride | WmiFlags.ToSubclass, handle.Qualifiers["key"]!.Flags);
        Assert.Equal(WmiType.UInt16Array, @class.Properties["Sizes"]!.Type);
        Assert.Equal(new object?[] { "1", "2" }, @class.Properties["Sizes"]!.Qualifiers["ValueMap"]!.Value);
        Assert.Equal(WmiType.StringArray, @class.Properties["Sizes"]!.Qualifiers["ValueMap"]!.Type);
        Assert.Equal(WmiType.Instance, @class.Properties["Startup"]!.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.NullValue, @class.Properties["Gone"]!.Flags);
        Assert.Equal(3, @class.Properties["Level"]!.Value);
        Assert.Equal(WmiFlags.Property, @class.Properties["Level"]!.Flags);
        Assert.Equal(WmiType.Reference, @class.Properties["Owner"]!.Type);
        Assert.Equal("Win32_Account", @class.Properties["Owner"]!.ReferenceClassName);

        var create = @class.Methods["Create"]!;
        Assert.Equal(WmiType.UInt32, create.ReturnType);
        Assert.Equal("Static", Assert.Single(create.Qualifiers).Name);
        Assert.Equal(["CommandLine", "ProcessId", "Data"], create.Parameters.Select(p => p.Name));
        Assert.Equal(WmiFlags.Parameter | WmiFlags.In, create.Parameters["CommandLine"]!.Flags);
        Assert.Equal(WmiFlags.Parameter | WmiFlags.Out, create.Parameters["ProcessId"]!.Flags);
        Assert.Equal(WmiType.UInt8Array, create.Parameters["Data"]!.Type);
    }

    [Fact]
    public void A_WinRM_instance_is_typed_and_flagged_by_its_class_definition()
    {
        var schema = WsmanClassSchema.Parse(XElement.Parse(ThingClass), "h", "ns");
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

        Assert.Same(schema.Class, obj.Class);
        var handle = obj.Properties["Handle"]!;
        Assert.Equal(4u, handle.Value);
        Assert.Equal(WmiType.UInt32, handle.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NotModified, handle.Flags);
        Assert.Equal(WmiType.UInt16Array, obj.Properties["Sizes"]!.Type);
        Assert.Equal(new object?[] { (ushort)1 }, obj["Sizes"]);
        Assert.Equal(WmiType.Instance, obj.Properties["Startup"]!.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.NullValue | WmiFlags.NotModified, obj.Properties["Gone"]!.Flags);
        Assert.Equal(WmiType.Unknown, obj.Properties["Extra"]!.Type);
        Assert.Equal("Win32_Thing.Handle=4", obj.SystemProperties.RelativePath);
    }

    [Fact]
    public void A_WinRM_instance_without_a_class_definition_is_untyped_except_for_embedded_objects()
    {
        var instance = XElement.Parse("""
            <p:Win32_Thing xmlns:p="urn:x" xmlns:i="urn:y" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <p:Handle>4</p:Handle>
              <p:Startup xsi:type="i:Win32_ProcessStartup_Type"><i:ShowWindow>1</i:ShowWindow></p:Startup>
            </p:Win32_Thing>
            """);

        var obj = WsmanObjectReader.Read(instance, null, ["Handle"], "h", "ns");

        Assert.Equal(WmiType.Unknown, obj.Properties["Handle"]!.Type);
        Assert.True(obj.Properties["Handle"]!.Flags.HasFlag(WmiFlags.Key));
        Assert.Equal(WmiType.Instance, obj.Properties["Startup"]!.Type);
        Assert.Equal(["Handle", "Startup"], obj.Class.Properties.Select(p => p.Name));
        Assert.True(obj.Class.Properties["Handle"]!.Flags.HasFlag(WmiFlags.Key));
        Assert.Empty(obj.Class.Methods);
    }

    [Fact]
    public void A_WinRM_embedded_object_typed_as_a_string_is_an_instance_from_its_value()
    {
        // __InstanceCreationEvent.TargetInstance: a string with an EmbeddedObject qualifier, or, when the class gives no
        // qualifier, only the value says.
        var schema = WsmanClassSchema.Parse(XElement.Parse("""
            <CLASS NAME="NWmi_Event"><PROPERTY NAME="Target" TYPE="string"/></CLASS>
            """), null, null);
        var instance = XElement.Parse("""
            <p:NWmi_Event xmlns:p="urn:x" xmlns:i="urn:y" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <p:Target xsi:type="i:Win32_Process_Type"><i:Handle>4</i:Handle></p:Target>
            </p:NWmi_Event>
            """);

        var obj = WsmanObjectReader.Read(instance, null, [], null, null, null, _ => schema);

        Assert.Equal(WmiType.Instance, obj.Properties["Target"]!.Type);
        Assert.IsType<WmiInstance>(obj["Target"]);
    }
}
