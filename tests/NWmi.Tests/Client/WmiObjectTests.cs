using NWmi.Client;
using NWmi.Wmio;

namespace NWmi.Tests.Client;

public class WmiObjectTests
{
    private static WmioClass ProcessClass() => new(
        "Win32_Process",
        [],
        [],
        [
            new WmioProperty(
                "Handle", CimType.String, false, 0, 0,
                [new WmioQualifier("key", QualifierFlavor.NotOverridable, CimType.Boolean, true)],
                new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Caption", CimType.String, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 0),
        ],
        []);

    private static WmiObject Instance(string handle, string? caption, WmioDecoration? decoration = null, string? server = null, string? @namespace = null)
    {
        var values = new Dictionary<string, object?> { ["Handle"] = handle, ["Caption"] = caption };
        var block = WmioObjectBlock.ForInstance(WmioInstance.Create(ProcessClass(), values), decoration);
        return new WmiObject(block, server, @namespace);
    }

    [Fact]
    public void System_properties_are_synthesized_not_stored_as_ordinary_properties()
    {
        var obj = Instance("4", "notepad.exe", server: "server01", @namespace: @"root\cimv2");

        Assert.Equal("Win32_Process", obj["__CLASS"]);
        Assert.Equal("server01", obj["__SERVER"]);
        Assert.Equal(@"root\cimv2", obj["__NAMESPACE"]);
        Assert.Equal("Win32_Process.Handle=\"4\"", obj["__RELPATH"]);
        Assert.Equal(@"\\server01\root\cimv2:Win32_Process.Handle=""4""", obj["__PATH"]);
        Assert.DoesNotContain("__CLASS", obj.Properties.Keys);
    }

    [Fact]
    public void A_bare_class_object_has_the_class_name_as_its_relative_path_until_its_keys_have_values()
    {
        var template = new WmiObject(WmioObjectBlock.ForClass(ProcessClass()), "server01", @"root\cimv2");

        Assert.Equal("Win32_Process", template.RelativePath); // used to throw: a key property value cannot be null
        Assert.Equal(@"\\server01\root\cimv2:Win32_Process", template.Path);
        Assert.True(template.ObjectPath.IsClass);

        template["Handle"] = "4";

        Assert.Equal("Win32_Process.Handle=\"4\"", template.RelativePath);
        Assert.Equal("4", template.ObjectPath["Handle"]);
    }

    [Fact]
    public void ObjectPath_is_the_parsed_path()
    {
        var path = Instance("4", null, server: "server01", @namespace: @"root\cimv2").ObjectPath;

        Assert.Equal("server01", path.Server);
        Assert.Equal(@"root\cimv2", path.Namespace);
        Assert.Equal("Win32_Process", path.ClassName);
        Assert.Equal("4", path["Handle"]);
    }

    [Fact]
    public void Wire_decoration_overrides_the_connections_server_and_namespace_hints()
    {
        var obj = Instance("4", "notepad.exe", new WmioDecoration("real-server", @"root\default"), server: "hint-server", @namespace: @"root\cimv2");

        Assert.Equal("real-server", obj["__SERVER"]);
        Assert.Equal(@"root\default", obj["__NAMESPACE"]);
    }

    [Fact]
    public void Ordinary_properties_are_read_and_written_through_the_indexer()
    {
        var obj = Instance("4", "notepad.exe");

        Assert.Equal("notepad.exe", obj["Caption"]);
        obj["Caption"] = "changed.exe";
        Assert.Equal("changed.exe", obj["Caption"]);
        Assert.Equal("changed.exe", obj.Properties["Caption"]);
    }

    [Fact]
    public void Unknown_property_names_throw()
    {
        var obj = Instance("4", "notepad.exe");
        Assert.Throws<KeyNotFoundException>(() => obj["DoesNotExist"]);
        Assert.Throws<ArgumentException>(() => obj["DoesNotExist"] = 1);
    }

    [Fact]
    public void System_properties_cannot_be_set()
    {
        var obj = Instance("4", "notepad.exe");
        Assert.Throws<ArgumentException>(() => obj["__CLASS"] = "Other");
    }

    [Fact]
    public void A_class_without_keys_falls_back_to_the_class_name_as_its_relative_path()
    {
        var keylessClass = new WmioClass(
            "__PARAMETERS", [], [], [new WmioProperty("X", CimType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
        var block = WmioObjectBlock.ForInstance(WmioInstance.Create(keylessClass, new Dictionary<string, object?> { ["X"] = 1 }));
        var obj = new WmiObject(block, null, null);

        Assert.Equal("__PARAMETERS", obj.RelativePath);
        Assert.Null(obj.Path); // no namespace known
    }

    [Fact]
    public void A_bare_class_object_exposes_its_declared_defaults_as_starting_values()
    {
        var block = WmioObjectBlock.ForClass(ProcessClassWithDefault());
        var obj = new WmiObject(block, null, null);

        Assert.Equal("the default", obj["Caption"]);
        obj["Caption"] = "overridden";
        Assert.Equal("overridden", obj["Caption"]);
    }

    [Fact]
    public void Property_qualifiers_start_empty_and_can_be_set_replaced_and_removed()
    {
        var obj = Instance("4", "x");
        Assert.Empty(obj.GetPropertyQualifiers("Caption"));

        obj.SetPropertyQualifier("Caption", "Description", "first");
        obj.SetPropertyQualifier("Caption", "DESCRIPTION", "second"); // replaces, whatever the case
        obj.SetPropertyQualifier("Caption", "Hidden", true);
        var qualifiers = obj.GetPropertyQualifiers("Caption");
        Assert.Equal(2, qualifiers.Count);
        Assert.Equal("second", qualifiers["description"]);
        Assert.Empty(obj.GetPropertyQualifiers("Handle")); // other properties are unaffected

        Assert.True(obj.RemovePropertyQualifier("Caption", "hidden"));
        Assert.False(obj.RemovePropertyQualifier("Caption", "hidden"));
        Assert.False(obj.RemovePropertyQualifier("Handle", "anything"));
        Assert.Single(obj.GetPropertyQualifiers("Caption"));
    }

    [Fact]
    public void The_returned_qualifiers_are_a_copy()
    {
        var obj = Instance("4", "x");
        obj.SetPropertyQualifier("Caption", "A", 1);
        var before = obj.GetPropertyQualifiers("Caption");
        obj.SetPropertyQualifier("Caption", "B", 2);

        Assert.Single(before);
        Assert.Equal(2, obj.GetPropertyQualifiers("Caption").Count);
    }

    [Theory]
    [InlineData("text", (ushort)CimType.String)]
    [InlineData(true, (ushort)CimType.Boolean)]
    [InlineData((sbyte)-1, (ushort)CimType.Sint8)]
    [InlineData((byte)1, (ushort)CimType.Uint8)]
    [InlineData((short)-2, (ushort)CimType.Sint16)]
    [InlineData((ushort)2, (ushort)CimType.Uint16)]
    [InlineData(-3, (ushort)CimType.Sint32)]
    [InlineData(3U, (ushort)CimType.Uint32)]
    [InlineData(-4L, (ushort)CimType.Sint64)]
    [InlineData(4UL, (ushort)CimType.Uint64)]
    [InlineData(1.5F, (ushort)CimType.Real32)]
    [InlineData(2.5, (ushort)CimType.Real64)]
    public void A_qualifiers_CIM_type_comes_from_the_value_and_survives_encoding(object value, ushort type)
    {
        var obj = Instance("4", "x");
        obj.SetPropertyQualifier("Caption", "Q", value);

        var instance = WmioObjectBlock.DecodeEncodingUnit(obj.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        var qualifier = Assert.Single(instance.PropertyQualifiers!["Caption"]);
        Assert.Equal((CimType)type, qualifier.Type);
        Assert.Equal(value, qualifier.Value);
    }

    [Fact]
    public void Qualifier_arrays_take_their_type_from_the_elements_and_survive_encoding()
    {
        var obj = Instance("4", "x");
        obj.SetPropertyQualifier("Caption", "Strings", new List<string> { "a", "b" });
        obj.SetPropertyQualifier("Caption", "Ints", new[] { 1, 2, 3 });

        var instance = WmioObjectBlock.DecodeEncodingUnit(obj.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        var set = instance.PropertyQualifiers!["Caption"].ToDictionary(q => q.Name);
        Assert.Equal(CimType.String | CimType.ArrayFlag, set["Strings"].Type);
        Assert.Equal(new object?[] { "a", "b" }, set["Strings"].Value);
        Assert.Equal(CimType.Sint32 | CimType.ArrayFlag, set["Ints"].Type);
        Assert.Equal(new object?[] { 1, 2, 3 }, set["Ints"].Value);
        Assert.Empty(instance.PropertyQualifiers["Handle"]);
    }

    [Fact]
    public void Bad_qualifier_arguments_are_rejected_and_leave_the_object_unchanged()
    {
        var obj = Instance("4", "x");

        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("NoSuch", "Q", 1));
        Assert.Throws<ArgumentException>(() => obj.GetPropertyQualifiers("NoSuch"));
        Assert.Throws<ArgumentException>(() => obj.RemovePropertyQualifier("NoSuch", "Q"));
        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("Caption", "", 1));
        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("Caption", "Q", null!));
        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("Caption", "Q", Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("Caption", "Q", new object[] { "a", 1 }));
        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("Caption", "Q", new object?[] { "a", null }));
        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("Caption", "Q", new[] { new[] { 1 } }));
        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("Caption", "Q", new object()));
        Assert.Throws<ArgumentException>(() => obj.SetPropertyQualifier("Caption", "Q", DateTime.Now));

        Assert.Empty(obj.GetPropertyQualifiers("Caption"));
        Assert.Null(WmioObjectBlock.DecodeEncodingUnit(obj.ToInstanceBlock().EncodeEncodingUnit()).Instance!.PropertyQualifiers);
    }

    [Fact]
    public void A_WinRM_object_has_no_qualifiers_to_get_or_set()
    {
        var obj = new WmiObject("Win32_Process", [KeyValuePair.Create<string, object?>("Handle", "4")], ["Handle"], null, null);

        Assert.Throws<InvalidOperationException>(() => obj.GetPropertyQualifiers("Handle"));
        Assert.Throws<InvalidOperationException>(() => obj.SetPropertyQualifier("Handle", "Q", 1));
    }

    [Fact]
    public void Qualifiers_of_a_read_instance_are_kept_and_a_bare_class_object_starts_without_any()
    {
        var withQualifiers = WmioInstance.Create(
            ProcessClass(),
            new Dictionary<string, object?> { ["Handle"] = "4" },
            [new WmioQualifier("Provider", QualifierFlavor.None, CimType.String, "p")],
            new Dictionary<string, IReadOnlyList<WmioQualifier>> { ["Caption"] = [new WmioQualifier("Note", QualifierFlavor.None, CimType.String, "n")] });
        var read = new WmiObject(WmioObjectBlock.DecodeEncodingUnit(WmioObjectBlock.ForInstance(withQualifiers).EncodeEncodingUnit()), null, null);

        Assert.Equal("n", read.GetPropertyQualifiers("Caption")["Note"]);
        var again = WmioObjectBlock.DecodeEncodingUnit(read.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        Assert.Equal("p", Assert.Single(again.Qualifiers).Value);
        Assert.Equal("n", again.PropertyQualifiers!["Caption"].Single().Value);

        var bare = new WmiObject(WmioObjectBlock.ForClass(ProcessClass()), null, null);
        Assert.Empty(bare.GetPropertyQualifiers("Handle")); // the class's own "key" qualifier is not an instance qualifier
    }

    [Fact]
    public void Property_names_match_case_insensitively_and_keep_the_classs_spelling()
    {
        var obj = Instance("4", "x");

        Assert.Equal("x", obj["caption"]);
        Assert.Equal("x", obj["CAPTION"]);
        Assert.Equal("x", obj.Properties["caption"]);

        obj["cAPTION"] = "changed";

        Assert.Equal("changed", obj["Caption"]);
        Assert.Equal(["Handle", "Caption"], obj.Properties.Keys); // still the class's spelling, and no duplicate
        Assert.Throws<KeyNotFoundException>(() => obj["captions"]);
        Assert.Throws<ArgumentException>(() => obj["captions"] = "x");
    }

    [Fact]
    public void A_value_set_under_another_case_is_what_gets_encoded()
    {
        var obj = Instance("4", "x");
        obj["caption"] = "lower";
        obj.SetPropertyQualifier("HANDLE", "Q", 1);

        var instance = WmioObjectBlock.DecodeEncodingUnit(obj.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        Assert.Equal("lower", instance.Values["Caption"]);
        Assert.Equal("4", instance.Values["Handle"]);
        Assert.Single(instance.PropertyQualifiers!["Handle"]);
        Assert.Equal(["Handle"], obj.GetPropertyQualifiers("handle").Count == 1 ? ["Handle"] : []);
    }

    [Fact]
    public void A_bare_class_object_also_matches_names_case_insensitively()
    {
        var obj = new WmiObject(WmioObjectBlock.ForClass(ProcessClassWithDefault()), null, null);

        Assert.Equal("the default", obj["CAPTION"]);
        obj["handle"] = "7";
        Assert.Equal("7", obj["Handle"]);
    }

    [Fact]
    public void WmioInstance_Create_matches_value_and_qualifier_names_case_insensitively()
    {
        var instance = WmioInstance.Create(
            ProcessClass(),
            new Dictionary<string, object?> { ["handle"] = "4", ["CAPTION"] = "c" },
            propertyQualifiers: new Dictionary<string, IReadOnlyList<WmioQualifier>>
            {
                ["caption"] = [new WmioQualifier("Q", QualifierFlavor.None, CimType.Sint32, 1)],
            });

        Assert.Equal("4", instance.Values["Handle"]);
        Assert.Equal("c", instance.Values["Caption"]);
        Assert.Equal(["Caption"], instance.PropertyQualifiers!.Keys);
    }

    [Fact]
    public void WmioInstance_Create_rejects_the_same_property_given_twice_and_unknown_names()
    {
        Assert.Throws<ArgumentException>(() => WmioInstance.Create(
            ProcessClass(), new Dictionary<string, object?> { ["Caption"] = "a", ["caption"] = "b" }));
        Assert.Throws<ArgumentException>(() => WmioInstance.Create(
            ProcessClass(), new Dictionary<string, object?> { ["Captions"] = "a" }));
    }

    [Fact]
    public void Method_parameter_names_match_the_signature_case_insensitively()
    {
        var block = WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["handle"] = "9" }, ProcessClass());

        var property = block.Class!.Properties.Single(p => p.Name == "Handle");
        Assert.Equal("9", property.DefaultValue);
        Assert.Throws<ArgumentException>(() => WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["Handle"] = "1", ["HANDLE"] = "2" }, ProcessClass()));
        Assert.Throws<ArgumentException>(() => WmiConnection.BuildParametersBlock(
            new Dictionary<string, object?> { ["Nope"] = "1" }, ProcessClass()));
    }

    private static WmioClass ProcessClassWithDefault() => new(
        "Win32_Process",
        [],
        [],
        [
            new WmioProperty("Handle", CimType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Caption", CimType.String, false, 1, 0, [], new NullAndDefaultFlag(false, false), "the default", 0),
        ],
        []);
}
