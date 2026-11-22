using System.Xml.Linq;
using NWmi.Client;
using NWmi.WinRm;

namespace NWmi.Tests.WinRm;

public class WinRmValueEncodingTests
{
    private static readonly XNamespace Ns = "urn:test";

    private static XElement Encode(object? value)
    {
        var parent = new XElement("parent");
        WinRmBackend.AppendValue(parent, Ns + "Value", value);
        return parent;
    }

    [Fact]
    public void A_moment_goes_as_a_cim_Datetime()
    {
        var parent = Encode(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(2)));

        var value = Assert.Single(parent.Elements()).Elements().Single();
        Assert.Equal(Wsman.Cim + "Datetime", value.Name);
        Assert.Equal("2026-01-02T03:04:05.678000+02:00", value.Value);
    }

    [Fact]
    public void A_length_of_time_goes_as_a_cim_Interval()
    {
        var parent = Encode(new TimeSpan(1, 2, 3, 4, 500));

        var value = Assert.Single(parent.Elements()).Elements().Single();
        Assert.Equal(Wsman.Cim + "Interval", value.Name);
        Assert.Equal("P1DT2H3M4.5S", value.Value);
    }

    [Fact]
    public void A_negative_interval_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void Each_element_of_an_array_of_moments_is_encoded()
    {
        var moment = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var parent = Encode(new[] { moment, moment.AddDays(1) });

        Assert.Equal(2, parent.Elements().Count());
        Assert.All(parent.Elements(), e => Assert.Equal(Wsman.Cim + "Datetime", e.Elements().Single().Name));
    }

    private static WsmanClassSchema Schema() => WsmanClassSchema.Parse(XElement.Parse(
        """
        <CLASS NAME="Test">
          <PROPERTY NAME="When" TYPE="datetime"/>
          <PROPERTY.ARRAY NAME="Times" TYPE="datetime"/>
          <PROPERTY NAME="Label" TYPE="string"/>
        </CLASS>
        """));

    [Fact]
    public void A_DMTF_string_on_a_datetime_property_becomes_a_moment()
    {
        var value = WinRmBackend.ForWire("20260102030405.678000+120", "When", Schema());

        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(2)), value);
    }

    [Fact]
    public void A_DMTF_interval_on_a_datetime_property_becomes_a_length_of_time()
    {
        var value = WinRmBackend.ForWire("00000001020304.500000:000", "When", Schema());

        Assert.Equal(new TimeSpan(1, 2, 3, 4, 500), value);
    }

    [Fact]
    public void Each_string_of_a_datetime_array_is_converted()
    {
        var value = WinRmBackend.ForWire(new object?[] { "20260102030405.000000+000" }, "Times", Schema());

        Assert.IsType<DateTimeOffset>(Assert.Single(Assert.IsType<object?[]>(value)));
    }

    [Theory]
    [InlineData("Label", "20260102030405.000000+000")] // a string that only looks like one
    [InlineData("Unknown", "20260102030405.000000+000")]
    [InlineData("When", "not a date")]
    [InlineData("When", "2026****030405.000000+000")] // DMTF wildcards have no xs:dateTime form
    public void Anything_else_is_left_alone(string property, string text) =>
        Assert.Equal(text, WinRmBackend.ForWire(text, property, Schema()));

    [Fact]
    public void Without_a_schema_a_value_is_left_alone() =>
        Assert.Equal("20260102030405.000000+000", WinRmBackend.ForWire("20260102030405.000000+000", "When", null));

    private static WmiInstance Embedded(string className, string property, object? value) =>
        WmiInstance.FromWinRm(className, new Dictionary<string, object?> { [property] = value }, null, [], "host",
            @"root\default");

    private static IEnumerable<XElement> DatetimesIn(XElement parent) => parent.Descendants(Wsman.Cim + "Datetime");

    [Fact]
    public void A_DMTF_string_inside_an_embedded_object_is_converted()
    {
        var parent = new XElement("parent");

        WinRmBackend.AppendValue(parent, Ns + "Value", Embedded("Test", "When", "20260102030405.000000+120"), _ => Schema());

        Assert.Equal("2026-01-02T03:04:05.000000+02:00", Assert.Single(DatetimesIn(parent)).Value);
    }

    [Fact]
    public void A_DMTF_string_two_embedded_objects_deep_is_converted()
    {
        var parent = new XElement("parent");
        var outer = Embedded("Outer", "Inner", Embedded("Test", "When", "20260102030405.000000+120"));

        WinRmBackend.AppendValue(parent, Ns + "Value", outer, name => name == "Test" ? Schema() : null);

        Assert.Single(DatetimesIn(parent));
    }

    [Fact]
    public void Each_object_of_an_embedded_array_is_converted()
    {
        var parent = new XElement("parent");
        var items = new[] { Embedded("Test", "When", "20260102030405.000000+120"), Embedded("Test", "When", "20260103030405.000000+120") };

        WinRmBackend.AppendValue(parent, Ns + "Value", items, _ => Schema());

        Assert.Equal(2, DatetimesIn(parent).Count());
    }

    [Fact]
    public void A_string_property_of_an_embedded_object_stays_a_string()
    {
        var parent = new XElement("parent");

        WinRmBackend.AppendValue(parent, Ns + "Value", Embedded("Test", "Label", "20260102030405.000000+120"), _ => Schema());

        Assert.Empty(DatetimesIn(parent));
    }

    [Fact]
    public void What_is_written_is_what_the_reader_understands()
    {
        var moment = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));

        var written = Encode(moment).Elements().Single().Elements().Single().Value;

        Assert.Equal(moment.UtcDateTime, WsmanObjectReader.ParseDatetime(written));
    }
}
