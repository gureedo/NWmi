using NWmi.Wmio;

namespace NWmi.Tests.Wmio;

public class WmioInstanceEncodingTests
{
    private static WmioClass SimpleClass() => new(
        "TestClass",
        [],
        [],
        [
            new WmioProperty("Name", CimType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            new WmioProperty("Count", CimType.Sint32, false, 1, 0, [], new NullAndDefaultFlag(false, false), 7, 0),
            new WmioProperty("Flag", CimType.Boolean, false, 2, 0, [], new NullAndDefaultFlag(true, false), null, 0),
        ],
        []);

    [Fact]
    public void A_class_with_no_properties_round_trips()
    {
        var original = new WmioClass("Empty", [], [], [], []);

        var w = new WmioWriter();
        original.Write(w, hasMethodsPart: false);
        var decoded = WmioClass.Read(new WmioReader(w.ToArray()), hasMethodsPart: false);

        Assert.Equal("Empty", decoded.Name);
        Assert.Empty(decoded.Properties);
        Assert.Empty(decoded.DerivationList);
    }

    [Fact]
    public void A_class_with_properties_qualifiers_and_derivation_round_trips()
    {
        var original = new WmioClass(
            "Derived",
            ["Base", "GrandBase"],
            [new WmioQualifier("Description", QualifierFlavor.PropagateToInstance, CimType.String, "a class")],
            [
                new WmioProperty(
                    "Id", CimType.Sint32, true, 0, 1,
                    [new WmioQualifier("key", QualifierFlavor.NotOverridable, CimType.Boolean, true)],
                    new NullAndDefaultFlag(false, false), 42, 0),
                new WmioProperty(
                    "Tags", CimType.String | CimType.ArrayFlag, false, 1, 2, [],
                    new NullAndDefaultFlag(true, false), null, 0),
            ],
            []);

        var w = new WmioWriter();
        original.Write(w, hasMethodsPart: false);
        var decoded = WmioClass.Read(new WmioReader(w.ToArray()), hasMethodsPart: false);

        Assert.Equal("Derived", decoded.Name);
        Assert.Equal(["Base", "GrandBase"], decoded.DerivationList);
        Assert.Equal("a class", decoded.Qualifiers.Single().Value);

        var id = decoded.FindProperty("Id")!;
        Assert.Equal(CimType.Sint32, id.Type);
        Assert.True(id.DefinedInParentClass);
        Assert.Equal(1u, id.ClassOfOrigin);
        Assert.Equal(42, id.DefaultValue);
        Assert.Equal(true, id.Qualifiers.Single().Value);

        var tags = decoded.FindProperty("Tags")!;
        Assert.Equal(CimType.String | CimType.ArrayFlag, tags.Type);
        Assert.Null(tags.DefaultValue);
    }

    [Fact]
    public void A_class_with_methods_round_trips()
    {
        var original = new WmioClass(
            "WithMethods",
            [],
            [],
            [],
            [new WmioMethod("DoThing", false, 0, [1, 2, 3], [4, 5])]);

        var w = new WmioWriter();
        original.Write(w, hasMethodsPart: true);
        var decoded = WmioClass.Read(new WmioReader(w.ToArray()), hasMethodsPart: true);

        var method = Assert.Single(decoded.Methods);
        Assert.Equal("DoThing", method.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, method.InputSignatureRaw);
        Assert.Equal(new byte[] { 4, 5 }, method.OutputSignatureRaw);
    }

    [Fact]
    public void An_instance_built_with_explicit_values_round_trips_through_an_ObjectBlock()
    {
        var @class = SimpleClass();
        var instance = WmioInstance.Create(@class, new Dictionary<string, object?>
        {
            ["Name"] = "hello",
            ["Flag"] = true,
        });

        var block = WmioObjectBlock.ForInstance(instance);
        var bytes = block.EncodeEncodingUnit();
        var decoded = WmioObjectBlock.DecodeEncodingUnit(bytes);

        Assert.False(decoded.IsClass);
        Assert.Equal("hello", decoded.Instance!.Values["Name"]);
        Assert.Equal(7, decoded.Instance.Values["Count"]); // not supplied: falls back to the class default
        Assert.Equal(true, decoded.Instance.Values["Flag"]);
    }

    [Fact]
    public void An_instance_with_decoration_round_trips()
    {
        var @class = SimpleClass();
        var instance = WmioInstance.Create(@class);
        var block = WmioObjectBlock.ForInstance(instance, new WmioDecoration("SERVER01", @"root\cimv2"));

        var decoded = WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit());

        Assert.Equal("SERVER01", decoded.Decoration!.Server);
        Assert.Equal(@"root\cimv2", decoded.Decoration.Namespace);
    }

    [Fact]
    public void Omitted_properties_use_the_class_default_even_when_it_is_null()
    {
        var @class = SimpleClass();
        var instance = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Count"] = 99 });

        var decoded = WmioObjectBlock.DecodeEncodingUnit(WmioObjectBlock.ForInstance(instance).EncodeEncodingUnit());

        Assert.Null(decoded.Instance!.Values["Name"]); // class default is null, and wasn't overridden
        Assert.Equal(99, decoded.Instance.Values["Count"]);
    }

    [Fact]
    public void An_explicit_null_overrides_a_non_null_class_default()
    {
        var @class = SimpleClass();
        var instance = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Count"] = null });

        var decoded = WmioObjectBlock.DecodeEncodingUnit(WmioObjectBlock.ForInstance(instance).EncodeEncodingUnit());

        Assert.Null(decoded.Instance!.Values["Count"]);
    }

    [Fact]
    public void Arrays_and_embedded_objects_round_trip_through_an_instance()
    {
        var innerClass = new WmioClass(
            "Inner", [], [],
            [new WmioProperty("X", CimType.Sint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)],
            []);
        var inner = WmioInstance.Create(innerClass, new Dictionary<string, object?> { ["X"] = 5 });
        var innerBlock = WmioObjectBlock.ForInstance(inner);

        var outerClass = new WmioClass(
            "Outer", [], [],
            [
                new WmioProperty("Numbers", CimType.Sint32 | CimType.ArrayFlag, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
                new WmioProperty("Nested", CimType.Object, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            ],
            []);
        var outer = WmioInstance.Create(outerClass, new Dictionary<string, object?>
        {
            ["Numbers"] = new object?[] { 10, 20, 30 },
            ["Nested"] = innerBlock,
        });

        var decoded = WmioObjectBlock.DecodeEncodingUnit(WmioObjectBlock.ForInstance(outer).EncodeEncodingUnit());

        Assert.Equal(new object?[] { 10, 20, 30 }, decoded.Instance!.Values["Numbers"]);
        var nested = Assert.IsType<WmioObjectBlock>(decoded.Instance.Values["Nested"]);
        Assert.Equal(5, nested.Instance!.Values["X"]);
    }

    [Fact]
    public void A_property_not_on_the_class_is_rejected()
    {
        var @class = SimpleClass();
        Assert.Throws<ArgumentException>(
            () => WmioInstance.Create(@class, new Dictionary<string, object?> { ["DoesNotExist"] = 1 }));
    }

    [Fact]
    public void A_wrong_typed_value_is_rejected_when_encoding()
    {
        var @class = SimpleClass();
        var instance = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Count"] = "not a number" });
        Assert.Throws<ArgumentException>(() => WmioObjectBlock.ForInstance(instance).EncodeEncodingUnit());
    }

    [Fact]
    public void A_class_ObjectBlock_round_trips_through_a_degenerate_parent_class()
    {
        // A method's input/output parameters (MS-WMIO 2.3.3) travel as a class, not an instance: confirmed
        // against a real server's ExecMethod request. The ObjectBlock format always carries a ParentClass
        // ahead of the real one; for a class with no real superclass this is the empty placeholder a real
        // server itself sends (name "", no properties), which this round trip exercises implicitly.
        var block = WmioObjectBlock.ForClass(SimpleClass());

        var decoded = WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit());

        Assert.True(decoded.IsClass);
        Assert.Equal("TestClass", decoded.Class!.Name);
        Assert.Equal(7, decoded.Class.FindProperty("Count")!.DefaultValue);
        Assert.Null(decoded.Class.FindProperty("Name")!.DefaultValue);
    }

    [Fact]
    public void An_instance_of_a_decoded_class_reuses_the_original_class_bytes_and_value_table_layout()
    {
        // Properties sorted by name, but ValueTable offsets assigned in declaration order (Z first), as a real server does.
        var original = new WmioClass(
            "Layout", [], [],
            [
                new WmioProperty("A", CimType.Uint16, false, 1, 0, [], new NullAndDefaultFlag(true, false), null, 4),
                new WmioProperty("Z", CimType.Uint32, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0),
            ],
            []);
        var classWriter = new WmioWriter();
        original.Write(classWriter, hasMethodsPart: false);
        var classBytes = classWriter.ToArray();
        var decoded = WmioClass.Read(new WmioReader(classBytes), hasMethodsPart: false);
        Assert.Equal(classBytes, decoded.RawClassPart);

        var instance = WmioInstance.Create(decoded, new Dictionary<string, object?> { ["A"] = (ushort)7, ["Z"] = 9u });
        var w = new WmioWriter();
        instance.Write(w);
        Assert.True(w.WrittenSpan.StartsWith(classBytes));

        var roundTripped = WmioInstance.Read(new WmioReader(w.ToArray()));
        Assert.Equal((ushort)7, roundTripped.Values["A"]);
        Assert.Equal(9u, roundTripped.Values["Z"]);
    }

    [Fact]
    public void An_embedded_object_is_read_even_when_the_server_marks_it_null_and_inherited()
    {
        var inner = WmioObjectBlock.ForInstance(WmioInstance.Create(SimpleClass(), new Dictionary<string, object?> { ["Count"] = 3 }));
        var outer = new WmioClass(
            "Event", [], [],
            [new WmioProperty("Target", CimType.Object, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)],
            []);
        var instance = WmioInstance.Create(outer, new Dictionary<string, object?> { ["Target"] = inner });
        var w = new WmioWriter();
        instance.Write(w);
        var bytes = w.ToArray();

        // Set the instance NdTable's bits for the one property to "NULL and inherited" (0b11), as a real event does.
        var classLength = BitConverter.ToInt32(bytes, 0);
        bytes[classLength + 9] = 0b11;

        var read = WmioInstance.Read(new WmioReader(bytes));
        var embedded = Assert.IsType<WmioObjectBlock>(read.Values["Target"]);
        Assert.Equal(3, embedded.Instance!.Values["Count"]);
    }

    [Fact]
    public void A_string_slot_of_zero_reads_as_null_not_as_the_class_name_at_heap_offset_zero()
    {
        var @class = new WmioClass(
            "Holder", [], [],
            [new WmioProperty("Text", CimType.String, false, 0, 0, [], new NullAndDefaultFlag(true, false), null, 0)],
            []);
        var instance = WmioInstance.Create(@class, new Dictionary<string, object?> { ["Text"] = "hello" });
        var w = new WmioWriter();
        instance.Write(w);
        var bytes = w.ToArray();
        var classLength = BitConverter.ToInt32(bytes, 0);
        var valueTableStart = classLength + 9 + 1; // body encLen, flags, classNameRef, then a 1-octet NdTable
        Assert.Equal("hello", WmioInstance.Read(new WmioReader(bytes)).Values["Text"]);

        BitConverter.TryWriteBytes(bytes.AsSpan(valueTableStart, 4), 0u);
        Assert.Null(WmioInstance.Read(new WmioReader(bytes)).Values["Text"]);
    }
}
