using NWmi.Wmio;

namespace NWmi.Tests.Wmio;

public class WmioInheritedDefaultTests
{
    private const byte FlagClass = 0x01;

    private static WmioClass WithProperty(string name, NullAndDefaultFlag flag, object? value, bool inherited) => new(
        name, inherited ? ["Base"] : [], [],
        [new WmioProperty("Prop", WbemType.Uint32, inherited, 0, 0, [], flag, value, 0)], []);

    private static WmioClass Decode(WmioClass parent, WmioClass current)
    {
        var writer = new WmioWriter();
        writer.WriteByte(FlagClass);
        parent.Write(writer, hasMethodsPart: true);
        current.Write(writer, hasMethodsPart: true);
        return WmioObjectBlock.Decode(new WmioReader(writer.ToArray())).Class!;
    }

    [Fact]
    public void A_class_takes_an_inherited_default_from_its_parent_class()
    {
        var decoded = Decode(
            WithProperty("Base", new NullAndDefaultFlag(false, false), 5u, inherited: false),
            WithProperty("Derived", new NullAndDefaultFlag(false, true), null, inherited: true));

        Assert.Equal(5u, decoded.FindProperty("Prop")!.DefaultValue);
    }

    /// <summary>
    /// The class embedded in a real instance (Windows Server, root\default): NWmiDerived, whose Color is inherited from NWmiBase with
    /// the default "blue", and whose key Id has none. Windows copies the default into the derived class, and no parent comes with it.
    /// </summary>
    [Fact]
    public void Without_a_parent_class_an_inherited_default_comes_from_the_copy_in_the_derived_class()
    {
        const string RealClassPart =
            "F300000000000000000900000012000000004E576D6942617365000A00000004000000020000000D000000140000003B0000003F000000"
            + "3BFFFFFFFF71000000AF000080004E576D69446572697665640000436F6C6F72000840000001000400000000000000110000000A000080"
            + "23080000003300000000737472696E67000049640008400000000000000000000000001C0000000A00008023080000006900000001000080"
            + "330B000000FFFF00737472696E670000626C7565000000000000000000000000000000000000000000000000000000000000000000000000"
            + "000000000000000000000000000000000000000000";

        var decoded = WmioClass.Read(new WmioReader(Convert.FromHexString(RealClassPart)), hasMethodsPart: false);

        Assert.Equal("blue", decoded.FindProperty("Color")!.DefaultValue);
        Assert.Null(decoded.FindProperty("Id")!.DefaultValue);
    }

    [Fact]
    public void A_default_of_its_own_wins_over_the_parents()
    {
        var decoded = Decode(
            WithProperty("Base", new NullAndDefaultFlag(false, false), 5u, inherited: false),
            WithProperty("Derived", new NullAndDefaultFlag(false, false), 9u, inherited: true));

        Assert.Equal(9u, decoded.FindProperty("Prop")!.DefaultValue);
    }

    [Fact]
    public void A_null_default_stays_null_whatever_the_parent_says()
    {
        var decoded = Decode(
            WithProperty("Base", new NullAndDefaultFlag(false, false), 5u, inherited: false),
            WithProperty("Derived", new NullAndDefaultFlag(true, false), null, inherited: true));

        Assert.Null(decoded.FindProperty("Prop")!.DefaultValue);
    }
}
