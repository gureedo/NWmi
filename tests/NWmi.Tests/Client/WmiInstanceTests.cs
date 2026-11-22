using NWmi.Client;
using NWmi.Wmio;

namespace NWmi.Tests.Client;

public class WmiInstanceTests
{
    private static WmioClass ProcessClass() => new(
        "Win32_Process",
        [],
        [],
        [
            new WmioProperty(
                "Handle", WbemType.String, false, 0, 0,
                [new WmioQualifier("key", QualifierFlavor.NotOverridable, WbemType.Boolean, true)],
                new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Caption", WbemType.String, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 0),
        ],
        []);

    private static WmiInstance Instance(string handle, string? caption, WmioDecoration? decoration = null,
        string? server = null, string? @namespace = null)
    {
        var values = new Dictionary<string, object?> { ["Handle"] = handle, ["Caption"] = caption };
        var block = WmioObjectBlock.ForInstance(WmioInstance.Create(ProcessClass(), values), decoration);
        return WmiInstance.FromWire(block, server, @namespace);
    }

    private static WmiClass Class(WmioClass @class, string? server = null, string? @namespace = null) =>
        WmiClass.FromWire(@class, server, @namespace);

    [Fact]
    public void System_properties_are_synthesized_not_stored_as_ordinary_properties()
    {
        var system = Instance("4", "notepad.exe", server: "server01", @namespace: @"root\cimv2").SystemProperties;

        Assert.Equal("Win32_Process", system.ClassName);
        Assert.Equal("server01", system.Server);
        Assert.Equal(@"root\cimv2", system.Namespace);
        Assert.Equal("Win32_Process.Handle=\"4\"", system.RelativePath);
        Assert.Equal(@"\\server01\root\cimv2:Win32_Process.Handle=""4""", system.Path);
    }

    [Fact]
    public void System_property_names_are_not_properties()
    {
        var obj = Instance("4", "notepad.exe", server: "server01", @namespace: @"root\cimv2");

        Assert.False(obj.Properties.Contains("__CLASS"));
        Assert.Throws<KeyNotFoundException>(() => obj["__PATH"]);
        Assert.Throws<ArgumentException>(() => obj["__CLASS"] = "Other");
    }

    [Fact]
    public void The_path_follows_the_key_values()
    {
        var template = new WmiInstance(Class(ProcessClass(), "server01", @"root\cimv2"));

        Assert.Equal("Win32_Process", template.SystemProperties.RelativePath);
        Assert.Equal(@"\\server01\root\cimv2:Win32_Process", template.SystemProperties.Path);

        template["Handle"] = "4";

        Assert.Equal("Win32_Process.Handle=\"4\"", template.SystemProperties.RelativePath);
    }

    [Fact]
    public void Wire_decoration_overrides_the_connections_server_and_namespace_hints()
    {
        var obj = Instance("4", "notepad.exe", new WmioDecoration("real-server", @"root\default"), server: "hint-server",
            @namespace: @"root\cimv2");

        Assert.Equal("real-server", obj.SystemProperties.Server);
        Assert.Equal(@"root\default", obj.SystemProperties.Namespace);
        Assert.Equal(@"root\default", obj.Class.SystemProperties.Namespace);
    }

    [Fact]
    public void Ordinary_properties_are_read_and_written_through_the_indexer_and_the_collection()
    {
        var obj = Instance("4", "notepad.exe");

        Assert.Equal("notepad.exe", obj["Caption"]);
        obj["Caption"] = "changed.exe";
        Assert.Equal("changed.exe", obj["Caption"]);
        Assert.Equal("changed.exe", obj.Properties["Caption"]!.Value);

        obj.Properties["Caption"]!.Value = "again.exe";
        Assert.Equal("again.exe", obj["Caption"]);
    }

    [Fact]
    public void Unknown_property_names_throw_on_the_indexer_and_are_null_in_the_collection()
    {
        var obj = Instance("4", "notepad.exe");
        Assert.Throws<KeyNotFoundException>(() => obj["DoesNotExist"]);
        Assert.Throws<ArgumentException>(() => obj["DoesNotExist"] = 1);
        Assert.Null(obj.Properties["DoesNotExist"]);
    }

    [Fact]
    public void A_keyless_instance_has_the_class_name_as_its_relative_path()
    {
        var keylessClass = new WmioClass(
            "__PARAMETERS", [], [],
            [new WmioProperty("X", WbemType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
        var block = WmioObjectBlock.ForInstance(
            WmioInstance.Create(keylessClass, new Dictionary<string, object?> { ["X"] = 1 }));
        var obj = WmiInstance.FromWire(block, null, null);

        Assert.Equal("__PARAMETERS", obj.SystemProperties.RelativePath);
        Assert.Null(obj.SystemProperties.Path); // no namespace known
    }

    [Fact]
    public void An_instance_made_from_a_class_starts_with_its_declared_defaults()
    {
        var obj = new WmiInstance(Class(ProcessClassWithDefault()));

        Assert.Equal("the default", obj["Caption"]);
        Assert.False(obj.Properties["Caption"]!.IsValueModified);
        obj["Caption"] = "overridden";
        Assert.Equal("overridden", obj["Caption"]);
    }

    [Fact]
    public void A_class_returned_by_a_query_is_an_instance_with_no_path_and_null_values()
    {
        var row = WmiInstance.FromWire(WmioObjectBlock.ForClass(ProcessClassWithDefault()), "server01", @"root\cimv2");

        Assert.Equal("Win32_Process", row.SystemProperties.ClassName);
        Assert.Equal(@"root\cimv2", row.SystemProperties.Namespace);
        Assert.Null(row.SystemProperties.Path);
        Assert.Null(row.SystemProperties.RelativePath);
        Assert.All(row.Properties, p => Assert.Null(p.Value));
        Assert.Equal(WmiFlags.Property | WmiFlags.NullValue | WmiFlags.NotModified, row.Properties["Caption"]!.Flags);
        Assert.Equal("the default", row.Class.Properties["Caption"]!.Value); // the class keeps the default
    }

    [Fact]
    public void Setting_a_value_marks_it_modified_and_the_mark_can_be_cleared()
    {
        var obj = Instance("4", "x");
        var caption = obj.Properties["Caption"]!;
        Assert.False(caption.IsValueModified);
        Assert.True(caption.Flags.HasFlag(WmiFlags.NotModified));

        obj["Caption"] = "y";
        Assert.True(caption.IsValueModified);
        Assert.False(caption.Flags.HasFlag(WmiFlags.NotModified));
        Assert.False(obj.Properties["Handle"]!.IsValueModified);

        caption.IsValueModified = false;
        Assert.True(caption.Flags.HasFlag(WmiFlags.NotModified));
    }

    [Fact]
    public void A_datetime_set_as_a_DateTimeOffset_or_TimeSpan_is_stored_as_a_DMTF_string()
    {
        var @class = new WmioClass("NWmi_Dates", [], [],
        [
            new WmioProperty("When", WbemType.DateTime, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
        ], []);
        var obj = new WmiInstance(Class(@class));

        obj["When"] = new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.FromHours(1));
        Assert.Equal("20261121093000.000000+060", obj["When"]);

        obj.Properties["When"]!.Value = TimeSpan.FromMinutes(90);
        Assert.Equal("00000000013000.000000:000", obj["When"]);
    }

    [Fact]
    public void The_instances_class_carries_its_declarations_but_no_methods()
    {
        var obj = Instance("4", "x", server: "server01", @namespace: @"root\cimv2");

        var @class = obj.Class;
        Assert.Equal("Win32_Process", @class.Name);
        Assert.Equal(["Handle", "Caption"], @class.Properties.Select(p => p.Name));
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.NullValue, @class.Properties["handle"]!.Flags);
        Assert.Empty(@class.Methods);
        Assert.Same(@class, obj.Class);
    }

    [Fact]
    public void A_client_only_instance_takes_properties_and_its_class_is_made_from_them()
    {
        var obj = new WmiInstance("NWmi_Thing", @"root\default");
        obj.Properties.Add(WmiProperty.Create("Name", "one", WmiFlags.Key));
        obj.Properties.Add(WmiProperty.Create("Size", 5u, WmiType.UInt32, WmiFlags.None));

        Assert.Equal(WmiType.String, obj.Properties["Name"]!.Type);
        Assert.True(obj.Properties["Name"]!.IsValueModified);
        Assert.Equal(WmiFlags.Key, obj.Properties["Name"]!.Flags);
        Assert.Equal("NWmi_Thing.Name=\"one\"", obj.SystemProperties.RelativePath);
        Assert.Equal(["Name", "Size"], obj.Class.Properties.Select(p => p.Name));
        Assert.Equal(WmiType.UInt32, obj.Class.Properties["Size"]!.Type);

        obj.Properties.Add(WmiProperty.Create("Extra", true, WmiFlags.None));
        Assert.Equal(3, obj.Class.Properties.Count);

        Assert.Throws<ArgumentException>(() => obj.Properties.Add(WmiProperty.Create("name", "dup", WmiFlags.None)));
        Assert.Throws<ArgumentException>(() => WmiProperty.Create("__CLASS", "x", WmiFlags.None));
        Assert.Throws<ArgumentException>(() => WmiProperty.Create("X", "x", WmiType.Unknown, WmiFlags.None));
        Assert.Throws<InvalidOperationException>(() => obj.SetPropertyQualifier("Name", "Q", 1));
    }

    [Fact]
    public void An_instance_with_a_class_from_the_server_takes_no_new_properties()
    {
        var obj = Instance("4", "x");

        Assert.Throws<InvalidOperationException>(() => obj.Properties.Add(WmiProperty.Create("Extra", 1, WmiFlags.None)));
        Assert.Equal(2, obj.Properties.Count);
    }

    [Fact]
    public void A_copy_has_its_own_values_and_the_same_marks()
    {
        var embeddedClass = new WmioClass("NWmi_Inner", [], [],
            [new WmioProperty("V", WbemType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
        var outerClass = new WmioClass("NWmi_Outer", [], [],
        [
            new WmioProperty("Name", WbemType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Inner", WbemType.Object, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 0),
        ], []);
        var inner = WmioObjectBlock.ForInstance(WmioInstance.Create(embeddedClass, new Dictionary<string, object?> { ["V"] = 1 }));
        var original = WmiInstance.FromWire(
            WmioObjectBlock.ForInstance(WmioInstance.Create(outerClass,
                new Dictionary<string, object?> { ["Name"] = "a", ["Inner"] = inner })), "s", "ns");
        original["Name"] = "b";

        var copy = new WmiInstance(original);
        ((WmiInstance)copy["Inner"]!)["V"] = 2;
        copy["Name"] = "c";

        Assert.Equal("b", original["Name"]);
        Assert.Equal(1, ((WmiInstance)original["Inner"]!)["V"]);
        Assert.True(copy.Properties["Name"]!.IsValueModified);
        Assert.False(copy.Properties["Inner"]!.IsValueModified);
        Assert.Equal("s", copy.SystemProperties.Server);
        Assert.Equal("NWmi_Outer", copy.Class.Name);
        var encoded = WmioObjectBlock.DecodeEncodingUnit(copy.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        Assert.Equal(2, ((WmioObjectBlock)encoded.Values["Inner"]!).Instance!.Values["V"]);
    }

    [Fact]
    public void An_embedded_object_reads_as_an_instance_and_its_changes_are_encoded()
    {
        var embeddedClass = new WmioClass("NWmi_Inner", [], [],
            [new WmioProperty("V", WbemType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
        var outerClass = new WmioClass("NWmi_Outer", [], [],
            [new WmioProperty("Inner", WbemType.Object, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)], []);
        var inner = WmioObjectBlock.ForInstance(WmioInstance.Create(embeddedClass, new Dictionary<string, object?> { ["V"] = 1 }));
        var outer = WmiInstance.FromWire(WmioObjectBlock.ForInstance(
            WmioInstance.Create(outerClass, new Dictionary<string, object?> { ["Inner"] = inner })), null, null);

        var embedded = Assert.IsType<WmiInstance>(outer["Inner"]);
        Assert.Same(embedded, outer["Inner"]);
        Assert.Equal(WmiType.Instance, outer.Properties["Inner"]!.Type);
        embedded["V"] = 9;

        var encoded = WmioObjectBlock.DecodeEncodingUnit(outer.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        Assert.Equal(9, ((WmioObjectBlock)encoded.Values["Inner"]!).Instance!.Values["V"]);
    }

    [Fact]
    public void A_value_is_converted_to_its_declared_type_when_encoded()
    {
        var @class = new WmioClass("NWmi_Numbers", [], [],
        [
            new WmioProperty("Count", WbemType.Uint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Sizes", WbemType.Uint16.AsArray(), false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 0),
        ], []);
        var obj = new WmiInstance(Class(@class));
        obj["Count"] = 5;
        obj["Sizes"] = new List<int> { 1, 2 };

        var encoded = WmioObjectBlock.DecodeEncodingUnit(obj.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        Assert.Equal(5u, encoded.Values["Count"]);
        Assert.Equal(new object?[] { (ushort)1, (ushort)2 }, encoded.Values["Sizes"]);
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
    [InlineData("text", (ushort)WbemType.String)]
    [InlineData(true, (ushort)WbemType.Boolean)]
    [InlineData((sbyte)-1, (ushort)WbemType.Sint8)]
    [InlineData((byte)1, (ushort)WbemType.Uint8)]
    [InlineData((short)-2, (ushort)WbemType.Sint16)]
    [InlineData((ushort)2, (ushort)WbemType.Uint16)]
    [InlineData(-3, (ushort)WbemType.Sint32)]
    [InlineData(3U, (ushort)WbemType.Uint32)]
    [InlineData(-4L, (ushort)WbemType.Sint64)]
    [InlineData(4UL, (ushort)WbemType.Uint64)]
    [InlineData(1.5F, (ushort)WbemType.Real32)]
    [InlineData(2.5, (ushort)WbemType.Real64)]
    public void A_qualifiers_CIM_type_comes_from_the_value_and_survives_encoding(object value, ushort type)
    {
        var obj = Instance("4", "x");
        obj.SetPropertyQualifier("Caption", "Q", value);

        var instance = WmioObjectBlock.DecodeEncodingUnit(obj.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        var qualifier = Assert.Single(instance.PropertyQualifiers!["Caption"]);
        Assert.Equal((WbemType)type, qualifier.Type);
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
        Assert.Equal(WbemType.String | WbemType.ArrayFlag, set["Strings"].Type);
        Assert.Equal(new object?[] { "a", "b" }, set["Strings"].Value);
        Assert.Equal(WbemType.Sint32 | WbemType.ArrayFlag, set["Ints"].Type);
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
        Assert.Null(WmioObjectBlock.DecodeEncodingUnit(obj.ToInstanceBlock().EncodeEncodingUnit()).Instance!
            .PropertyQualifiers);
    }

    [Fact]
    public void A_WinRM_instance_has_no_qualifiers_to_get_or_set_and_cannot_be_encoded()
    {
        var obj = WmiInstance.FromWinRm("Win32_Process", [KeyValuePair.Create<string, object?>("Handle", "4")], null,
            ["Handle"], null, null);

        Assert.Throws<InvalidOperationException>(() => obj.GetPropertyQualifiers("Handle"));
        Assert.Throws<InvalidOperationException>(() => obj.SetPropertyQualifier("Handle", "Q", 1));
        Assert.Throws<InvalidOperationException>(() => obj.ToInstanceBlock());
    }

    [Fact]
    public void Qualifiers_of_a_read_instance_are_kept_and_a_new_instance_starts_without_any()
    {
        var withQualifiers = WmioInstance.Create(
            ProcessClass(),
            new Dictionary<string, object?> { ["Handle"] = "4" },
            [new WmioQualifier("Provider", QualifierFlavor.None, WbemType.String, "p")],
            new Dictionary<string, IReadOnlyList<WmioQualifier>>
                { ["Caption"] = [new WmioQualifier("Note", QualifierFlavor.None, WbemType.String, "n")] });
        var read = WmiInstance.FromWire(
            WmioObjectBlock.DecodeEncodingUnit(WmioObjectBlock.ForInstance(withQualifiers).EncodeEncodingUnit()), null,
            null);

        Assert.Equal("n", read.GetPropertyQualifiers("Caption")["Note"]);
        var again = WmioObjectBlock.DecodeEncodingUnit(read.ToInstanceBlock().EncodeEncodingUnit()).Instance!;
        Assert.Equal("p", Assert.Single(again.Qualifiers).Value);
        Assert.Equal("n", again.PropertyQualifiers!["Caption"].Single().Value);

        var bare = new WmiInstance(Class(ProcessClass()));
        Assert.Empty(bare.GetPropertyQualifiers("Handle")); // the class's own "key" qualifier is not an instance qualifier
    }

    [Fact]
    public void Property_names_match_case_insensitively_and_keep_the_classs_spelling()
    {
        var obj = Instance("4", "x");

        Assert.Equal("x", obj["caption"]);
        Assert.Equal("x", obj["CAPTION"]);
        Assert.Equal("x", obj.Properties["caption"]!.Value);

        obj["cAPTION"] = "changed";

        Assert.Equal("changed", obj["Caption"]);
        Assert.Equal(["Handle", "Caption"], obj.Properties.Select(p => p.Name)); // still the class's spelling, no duplicate
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
        Assert.Single(obj.GetPropertyQualifiers("handle"));
    }

    [Fact]
    public void WmioInstance_Create_matches_value_and_qualifier_names_case_insensitively()
    {
        var instance = WmioInstance.Create(
            ProcessClass(),
            new Dictionary<string, object?> { ["handle"] = "4", ["CAPTION"] = "c" },
            propertyQualifiers: new Dictionary<string, IReadOnlyList<WmioQualifier>>
            {
                ["caption"] = [new WmioQualifier("Q", QualifierFlavor.None, WbemType.Sint32, 1)],
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

    [Fact]
    public void A_keyed_collection_looks_names_up_case_insensitively_and_keeps_the_order()
    {
        var collection = new WmiReadOnlyKeyedCollection<WmiQualifier>(
            [new("b", 1, WmiType.SInt32, WmiFlags.None), new("A", 2, WmiType.SInt32, WmiFlags.None)], q => q.Name);

        Assert.Equal(["b", "A"], collection.Select(q => q.Name));
        Assert.Equal(2, collection["a"]!.Value);
        Assert.True(collection.Contains("B"));
        Assert.Null(collection["c"]);
        Assert.Equal(2, collection.Count);
    }

    private static WmioClass ProcessClassWithDefault() => new(
        "Win32_Process",
        [],
        [],
        [
            new WmioProperty("Handle", WbemType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Caption", WbemType.String, false, 1, 0, [], new NullAndDefaultFlag(false, false),
                "the default", 0),
        ],
        []);
}
