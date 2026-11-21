using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Tests.Wmio;

public class NdTableDeclarationOrderTests
{
    private static WmioProperty Property(string name, ushort declarationOrder, CimType type = CimType.Uint32) =>
        new(name, type, false, declarationOrder, 0, [], new NullAndDefaultFlag(true, false), null, 0);

    [Fact]
    public void Entries_are_placed_by_declaration_order_not_list_order()
    {
        // The list is alphabetical (A, B, C); the properties were declared C, A, B.
        WmioProperty[] properties = [Property("A", 1), Property("B", 2), Property("C", 0)];

        var entries = NdTable.InDeclarationOrder(properties, p => new NdTable.Entry(IsNull: p.Name != "A", IsInherited: false));

        // index 0 = C (null), 1 = A (set), 2 = B (null)
        Assert.Equal([true, false, true], entries.Select(e => e.IsNull));
        Assert.Equal(0b0001_0001, NdTable.Write(entries)[0]);
    }

    [Fact]
    public void A_class_without_a_meaningful_declaration_order_keeps_the_list_order()
    {
        WmioProperty[] allZero = [Property("A", 0), Property("B", 0), Property("C", 0)];
        WmioProperty[] gap = [Property("A", 0), Property("B", 5)];

        Assert.Equal([false, true, true], NdTable.InDeclarationOrder(allZero, p => new NdTable.Entry(p.Name != "A", false)).Select(e => e.IsNull));
        Assert.Equal([false, true], NdTable.InDeclarationOrder(gap, p => new NdTable.Entry(p.Name != "A", false)).Select(e => e.IsNull));
        Assert.False(NdTable.IsDeclarationOrdered([0, 0, 1], 3));
        Assert.True(NdTable.IsDeclarationOrdered([2, 0, 1], 3));
    }

    [Fact]
    public void A_declaration_order_outside_the_table_is_a_format_error_not_a_crash()
    {
        Assert.Throws<WmioFormatException>(() => NdTable.ReadEntry([0], 4, 3, "X"));
    }

    [Fact]
    public void An_instance_whose_properties_were_declared_in_another_order_than_their_names_round_trips()
    {
        var @class = new WmioClass("Thing", [], [], [Property("A", 2), Property("B", 0), Property("C", 1)], []);
        var instance = WmioInstance.Create(@class, new Dictionary<string, object?> { ["A"] = 10u, ["B"] = null, ["C"] = 30u });

        var decoded = WmioObjectBlock.DecodeEncodingUnit(WmioObjectBlock.ForInstance(instance).EncodeEncodingUnit()).Instance!;

        Assert.Equal(10u, decoded.Values["A"]);
        Assert.Null(decoded.Values["B"]);
        Assert.Equal(30u, decoded.Values["C"]);
    }

    /// <summary>
    /// What Windows' own client sent for <c>Win32_Process.Create("ping -n 40 127.0.0.1", null, startup)</c> with a
    /// <c>Win32_ProcessStartup</c> whose PriorityClass was 64 (idle) and ShowWindow 0, captured off the wire. The class
    /// lists its properties alphabetically but declares them in another order (PriorityClass 1st, ShowWindow 12th), and the
    /// instance marks exactly those two as set. Indexing the NdTable by list position read them as null.
    /// </summary>
    [Fact]
    public void A_real_clients_embedded_instance_is_read_by_declaration_order()
    {
        var inParams = WmiClassObjectCodec.Decode(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Wmio", "Fixtures", "win32_process_create_native_inparams.objref")));

        Assert.False(inParams.IsClass);
        Assert.Equal("ping -n 40 127.0.0.1", inParams.Instance!.Values["CommandLine"]);
        Assert.Null(inParams.Instance.Values["CurrentDirectory"]);

        var startup = Assert.IsType<WmioObjectBlock>(inParams.Instance.Values["ProcessStartupInformation"]).Instance!;
        Assert.Equal("Win32_ProcessStartup", startup.Class.Name);
        Assert.Equal(64u, startup.Values["PriorityClass"]);
        Assert.Equal((ushort)0, startup.Values["ShowWindow"]);
        Assert.Null(startup.Values["YCountChars"]); // the alphabetical neighbour that used to be read as ShowWindow
        Assert.Null(startup.Values["Title"]);
        Assert.Equal(1u, startup.Class.FindProperty("PriorityClass")!.DeclarationOrder);
    }
}
