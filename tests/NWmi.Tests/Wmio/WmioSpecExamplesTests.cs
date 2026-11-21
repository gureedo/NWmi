using NWmi.Wmio;

namespace NWmi.Tests.Wmio;

/// <summary>
/// Decodes the three worked examples straight from MS-WMIO section 3 (Structure Examples): the standalone
/// <c>base</c> class, the derived <c>MyClass</c> class, and an instance of <c>MyClass</c>. These are real,
/// spec-authored byte streams, not test data written for this library, so passing them is a much stronger
/// check than a self-consistent round trip.
/// </summary>
public class WmioSpecExamplesTests
{
    private static byte[] Hex(string text) =>
        Convert.FromHexString(text.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));

    private const string Base =
        "78 56 34 12 D0 00 00 00" +
        "05 00 44 50 52 41 56 41 54 2D 44 45" +
        "56 00 00 52 4F 4F 54 00 1D 00 00 00 00 FF FF FF" +
        "FF 00 00 00 00 04 00 00 00 04 00 00 00 00 00 00" +
        "00 00 00 00 80" +
        "0C 00 00 00 00 00 00 00 00 00 00 80" +
        "66 00 00 00 00 00 00 00 00 05 00 00 00 04 00" +
        "00 00 04 00 00 00 01 00 00 00 06 00 00 00 0A 00" +
        "00 00 05 FF FF FF FF 3C 00 00 80 00 42 61 73 65" +
        "00 00 49 64 00 03 00 00 00 00 00 00 00 00 00 00" +
        "00 00 00 1C 00 00 00 0A 00 00 80 03 08 00 00 00" +
        "34 00 00 00 01 00 00 80 13 0B 00 00 00 FF FF 00" +
        "73 69 6E 74 33 32 00 0C 00 00 00 00 00 34 00 00" +
        "00 00 80 00 80 13 0B 00 00 00 FF FF 00 73 69 6E" +
        "74 33 32 00";

    private const string MyClass =
        "78 56 34 12 2E 02 00 00 05 00 44 50" +
        "52 41 56 41 54 2D 44 45 56 00 00 52 4F 4F 54 00" +
        "66 00 00 00 00 00 00 00 00 05 00 00 00 04 00 00" +
        "00 04 00 00 00 01 00 00 00 06 00 00 00 0A 00 00" +
        "00 05 FF FF FF FF 3C 00 00 80 00 42 61 73 65 00" +
        "00 49 64 00 03 00 00 00 00 00 00 00 00 00 00 00" +
        "00 00 1C 00 00 00 0A 00 00 80 03 08 00 00 00 34" +
        "00 00 00 01 00 00 80 13 0B 00 00 00 FF FF 00 73" +
        "69 6E 74 33 32 00 0C 00 00 00 00 00 34 00 00 00" +
        "00 80 76 01 00 00 00 00 00 00 00 11 00 00 00 0E" +
        "00 00 00 00 42 61 73 65 00 06 00 00 00 11 00 00" +
        "00 09 00 00 00 00 08 00 00 00 16 00 00 00 04 00" +
        "00 00 27 00 00 00 2E 00 00 00 55 00 00 00 5C 00" +
        "00 00 99 00 00 00 A0 00 00 00 C7 00 00 00 CB 00" +
        "00 00 47 FF FF FF FF FF FF FF FF FD 00 00 00 FF" +
        "FF FF FF 11 01 00 80 00 4D 79 43 6C 61 73 73 00" +
        "00 44 65 73 63 72 69 70 74 69 6F 6E 00 00 4D 79" +
        "43 6C 61 73 73 20 45 78 61 6D 70 6C 65 00 00 41" +
        "72 72 61 79 00 13 20 00 00 03 00 0C 00 00 00 01" +
        "00 00 00 11 00 00 00 0A 00 00 80 03 08 00 00 00" +
        "4D 00 00 00 00 75 69 6E 74 33 32 00 00 44 61 74" +
        "61 31 00 08 00 00 00 01 00 04 00 00 00 01 00 00" +
        "00 27 00 00 00 0A 00 00 80 03 08 00 00 00 91 00" +
        "00 00 03 00 00 80 00 0B 00 00 00 FF FF 04 00 00" +
        "80 00 0B 00 00 00 FF FF 00 73 74 72 69 6E 67 00" +
        "00 44 61 74 61 32 00 08 00 00 00 02 00 08 00 00" +
        "00 01 00 00 00 11 00 00 00 0A 00 00 80 03 08 00" +
        "00 00 BF 00 00 00 00 73 74 72 69 6E 67 00 00 49" +
        "64 00 03 40 00 00 00 00 00 00 00 00 00 00 00 00" +
        "1C 00 00 00 0A 00 00 80 23 08 00 00 00 F5 00 00" +
        "00 01 00 00 80 33 0B 00 00 00 FF FF 00 73 69 6E" +
        "74 33 32 00 00 64 65 66 61 75 6C 74 56 61 6C 75" +
        "65 00 00 00 00 00 00 00 0C 00 00 00 00 00 00 73" +
        "00 00 00 80 32 00 00 64 65 66 61 75 6C 74 56 61" +
        "6C 75 65 00 00 00 00 00 00 00 00 00 00 00 00 00" +
        "00 00 00 00 00 80 00 00 00 00";

    private const string Instance =
        "78 56 34 12 D3 01 00 00 06 00 44 50" +
        "52 41 56 41 54 2D 44 45 56 00 00 52 4F 4F 54 00" +
        "76 01 00 00 00 00 00 00 00 11 00 00 00 0E 00 00" +
        "00 00 42 61 73 65 00 06 00 00 00 11 00 00 00 09" +
        "00 00 00 00 08 00 00 00 16 00 00 00 04 00 00 00" +
        "27 00 00 00 2E 00 00 00 55 00 00 00 5C 00 00 00" +
        "99 00 00 00 A0 00 00 00 C7 00 00 00 CB 00 00 00" +
        "47 FF FF FF FF FF FF FF FF FD 00 00 00 FF FF FF" +
        "FF 11 01 00 80 00 4D 79 43 6C 61 73 73 00 00 44" +
        "65 73 63 72 69 70 74 69 6F 6E 00 00 4D 79 43 6C" +
        "61 73 73 20 45 78 61 6D 70 6C 65 00 00 41 72 72" +
        "61 79 00 13 20 00 00 03 00 0C 00 00 00 01 00 00" +
        "00 11 00 00 00 0A 00 00 80 03 08 00 00 00 4D 00" +
        "00 00 00 75 69 6E 74 33 32 00 00 44 61 74 61 31" +
        "00 08 00 00 00 01 00 04 00 00 00 01 00 00 00 27" +
        "00 00 00 0A 00 00 80 03 08 00 00 00 91 00 00 00" +
        "03 00 00 80 00 0B 00 00 00 FF FF 04 00 00 80 00" +
        "0B 00 00 00 FF FF 00 73 74 72 69 6E 67 00 00 44" +
        "61 74 61 32 00 08 00 00 00 02 00 08 00 00 00 01" +
        "00 00 00 11 00 00 00 0A 00 00 80 03 08 00 00 00" +
        "BF 00 00 00 00 73 74 72 69 6E 67 00 00 49 64 00" +
        "03 40 00 00 00 00 00 00 00 00 00 00 00 00 1C 00" +
        "00 00 0A 00 00 80 23 08 00 00 00 F5 00 00 00 01" +
        "00 00 80 33 0B 00 00 00 FF FF 00 73 69 6E 74 33" +
        "32 00 00 64 65 66 61 75 6C 74 56 61 6C 75 65 00" +
        "00 00 00 00 00 00 49 00 00 00 00 00 00 00 00 20" +
        "7B 00 00 00 19 00 00 00 00 00 00 00 09 00 00 00" +
        "04 00 00 00 01 26 00 00 80 00 4D 79 43 6C 61 73" +
        "73 00 03 00 00 00 01 00 00 00 02 00 00 00" +
        "03 00 00 00 00 53 74 72 69 6E 67 46 69" +
        "65 6C 64 00";

    [Fact]
    public void The_base_class_decodes_correctly()
    {
        var block = WmioObjectBlock.DecodeEncodingUnit(Hex(Base));
        var @class = block.Class!;

        Assert.True(block.IsClass);
        Assert.Equal("DPRAVAT-DEV", block.Decoration!.Server);
        Assert.Equal("ROOT", block.Decoration.Namespace);
        Assert.Equal("Base", @class.Name);
        Assert.Empty(@class.DerivationList);

        var id = Assert.Single(@class.Properties);
        Assert.Equal("Id", id.Name);
        Assert.Equal(CimType.Sint32, id.Type);
        Assert.True(id.NdFlag.IsNull);
        Assert.False(id.NdFlag.IsInherited);
        Assert.Null(id.DefaultValue);

        Assert.Equal(2, id.Qualifiers.Count);
        var cimtype = id.Qualifiers.Single(q => q.Name == "CIMTYPE");
        Assert.Equal("sint32", cimtype.Value);
        var key = id.Qualifiers.Single(q => q.Name == "key");
        Assert.Equal(true, key.Value);
        Assert.Equal(QualifierFlavor.PropagateToInstance | QualifierFlavor.PropagateToDerivedClass, cimtype.Flavor);
    }

    [Fact]
    public void MyClass_decodes_with_its_full_merged_property_set()
    {
        var block = WmioObjectBlock.DecodeEncodingUnit(Hex(MyClass));
        var @class = block.Class!;

        Assert.Equal("MyClass", @class.Name);
        Assert.Equal(["Base"], @class.DerivationList);
        Assert.Equal(["Array", "Data1", "Data2", "Id"], @class.Properties.Select(p => p.Name));

        var description = @class.Qualifiers.Single(q => q.Name == "Description");
        Assert.Equal("MyClass Example", description.Value);

        var array = @class.FindProperty("Array")!;
        Assert.Equal(CimType.Uint32 | CimType.ArrayFlag, array.Type);
        Assert.False(array.DefinedInParentClass);
        Assert.Null(array.DefaultValue);

        var data1 = @class.FindProperty("Data1")!;
        Assert.Equal(CimType.String, data1.Type);
        Assert.Contains(data1.Qualifiers, q => q.Name == "read" && Equals(q.Value, true));
        Assert.Contains(data1.Qualifiers, q => q.Name == "write" && Equals(q.Value, true));
        Assert.Null(data1.DefaultValue);

        var data2 = @class.FindProperty("Data2")!;
        Assert.True(data2.NdFlag.HasLocalValue);
        Assert.Equal("defaultValue", data2.DefaultValue);

        var id = @class.FindProperty("Id")!;
        Assert.True(id.DefinedInParentClass);
        Assert.Equal(0u, id.ClassOfOrigin); // defined in DerivationList[0] = Base
        Assert.Null(id.DefaultValue);
    }

    [Fact]
    public void The_instance_resolves_explicit_values_and_falls_back_to_the_class_default()
    {
        var block = WmioObjectBlock.DecodeEncodingUnit(Hex(Instance));
        var instance = block.Instance!;

        Assert.False(block.IsClass);
        Assert.Equal("MyClass", instance.Class.Name);
        Assert.Equal(new object?[] { 1u, 2u, 3u }, instance.Values["Array"]); // uint32 Array[], so elements are uint
        Assert.Equal("StringField", instance.Values["Data1"]);
        Assert.Equal("defaultValue", instance.Values["Data2"]); // not set on the instance; inherited from the class default
        Assert.Equal(123, instance.Values["Id"]);
        Assert.Empty(instance.Qualifiers);
        Assert.Null(instance.PropertyQualifiers);
    }
}
