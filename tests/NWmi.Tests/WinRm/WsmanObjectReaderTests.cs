using System.Globalization;
using System.Xml.Linq;
using NWmi.Client;
using NWmi.WinRm;

namespace NWmi.Tests.WinRm;

public class WsmanObjectReaderTests
{
    private const string Instance = """
        <p:Win32_Thing xmlns:p="http://schemas.microsoft.com/wbem/wsman/1/wmi/root/cimv2/Win32_Thing"
            xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:cim="http://schemas.dmtf.org/wbem/wscim/1/common"
            xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd">
          <p:Handle>4</p:Handle>
          <p:Name>System</p:Name>
          <p:Enabled>true</p:Enabled>
          <p:Missing xsi:nil="true"/>
          <p:Created><cim:Datetime>2026-10-29T07:19:49.637856+01:00</cim:Datetime></p:Created>
          <p:Tags>a</p:Tags><p:Tags>b</p:Tags>
          <p:Child><p:Size>7</p:Size></p:Child>
        </p:Win32_Thing>
        """;

    [Fact]
    public void Values_are_mapped_by_their_shape()
    {
        var obj = WsmanObjectReader.Read(XElement.Parse(Instance), null, ["Handle"], "host", @"root\cimv2");

        Assert.Equal("Win32_Thing", obj.SystemProperties.ClassName);
        Assert.Equal("4", obj["Handle"]);
        Assert.Equal(true, obj["Enabled"]);
        Assert.Null(obj["Missing"]);
        Assert.Equal(new DateTime(2026, 10, 29, 6, 19, 49, DateTimeKind.Utc).AddTicks(6378560), obj["Created"]);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)obj["Created"]!).Kind);
        Assert.Equal(WmiType.DateTime, obj.Properties["Created"]!.Type);
        Assert.Equal(new object?[] { "a", "b" }, (object?[])obj["Tags"]!);
        Assert.Equal("7", ((WmiInstance)obj["Child"]!)["Size"]);
        Assert.EndsWith("Win32_Thing.Handle=\"4\"", obj.SystemProperties.Path);
    }

    [Fact]
    public void The_key_names_come_from_the_endpoint_reference_selectors()
    {
        var epr = XElement.Parse("""
            <a:EndpointReference xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd">
              <a:ReferenceParameters><w:SelectorSet><w:Selector Name="Handle">4</w:Selector></w:SelectorSet></a:ReferenceParameters>
            </a:EndpointReference>
            """);
        var obj = WsmanObjectReader.Read(XElement.Parse(Instance), epr, null, null, null);
        Assert.Equal(["Handle"], obj.KeyNames);
    }

    [Fact]
    public void An_association_keyed_by_references_has_only_its_own_keys_and_reads_its_references_as_instances()
    {
        const string reference = """
            <a:Address>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address>
            <a:ReferenceParameters>
              <w:ResourceURI>http://schemas.microsoft.com/wbem/wsman/1/wmi/root/cimv2/{0}</w:ResourceURI>
              <w:SelectorSet><w:Selector Name="Name">{1}</w:Selector></w:SelectorSet>
            </a:ReferenceParameters>
            """;
        var group = string.Format(CultureInfo.InvariantCulture, reference, "Win32_ComputerSystem", "W1");
        var part = string.Format(CultureInfo.InvariantCulture, reference, "Win32_Service", "WinRM");
        const string ns = """
            xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"
            """;
        var instance = XElement.Parse($"""
            <p:Win32_SystemServices xmlns:p="http://schemas.microsoft.com/wbem/wsman/1/wmi/root/cimv2/Win32_SystemServices" {ns}>
              <p:GroupComponent>{group}</p:GroupComponent>
              <p:PartComponent>{part}</p:PartComponent>
            </p:Win32_SystemServices>
            """);
        var epr = XElement.Parse($"""
            <a:EndpointReference {ns}>
              <a:Address>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address>
              <a:ReferenceParameters>
                <w:ResourceURI>http://schemas.microsoft.com/wbem/wsman/1/wmi/root/cimv2/Win32_SystemServices</w:ResourceURI>
                <w:SelectorSet>
                  <w:Selector Name="GroupComponent"><a:EndpointReference>{group}</a:EndpointReference></w:Selector>
                  <w:Selector Name="PartComponent"><a:EndpointReference>{part}</a:EndpointReference></w:Selector>
                </w:SelectorSet>
              </a:ReferenceParameters>
            </a:EndpointReference>
            """);

        var obj = WsmanObjectReader.Read(instance, epr, null, "host", @"root\cimv2");

        Assert.Equal(["GroupComponent", "PartComponent"], obj.KeyNames);
        var system = Assert.IsType<WmiInstance>(obj["GroupComponent"]);
        Assert.Equal(WmiType.Reference, obj.Properties["GroupComponent"]!.Type);
        Assert.Equal("Win32_ComputerSystem", system.SystemProperties.ClassName);
        Assert.Equal(@"root\cimv2", system.SystemProperties.Namespace);
        Assert.Equal("W1", system["Name"]);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.NotModified, system.Properties["Name"]!.Flags);
        Assert.Equal(@"root\cimv2:Win32_Service.Name=""WinRM""", ((WmiInstance)obj["PartComponent"]!).SystemProperties.Path);
        Assert.Equal(
            @"Win32_SystemServices.GroupComponent=""root\\cimv2:Win32_ComputerSystem.Name=\""W1\"""",PartComponent=""root\\cimv2:Win32_Service.Name=\""WinRM\""""",
            obj.SystemProperties.RelativePath);
    }

    [Fact]
    public void A_bare_fragment_takes_its_class_from_the_query()
    {
        var fragment = XElement.Parse("<w:XmlFragment xmlns:w=\"urn:x\"><Name>n</Name></w:XmlFragment>");
        var obj = WsmanObjectReader.Read(fragment, null, null, null, null,
            WsmanObjectReader.ClassOfQuery("select Name from Win32_Service where x = 1"));
        Assert.Equal("Win32_Service", obj.SystemProperties.ClassName);
    }

    [Theory]
    [InlineData("2026-01-02T03:04:05+00:00", "2026-01-02T03:04:05.0000000Z")]
    [InlineData("2026-01-02T03:04:05.5-05:30", "2026-01-02T08:34:05.5000000Z")]
    [InlineData("2026-01-02T03:04:05", "2026-01-02T03:04:05.0000000Z")] // no offset: UTC
    public void Datetimes_become_UTC_DateTimes(string iso, string expected)
    {
        var value = Assert.IsType<DateTime>(WsmanObjectReader.ParseDatetime(iso));
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(expected, value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_datetime_in_a_fragment_outside_the_cim_namespace_is_still_a_datetime()
    {
        var fragment = XElement.Parse("""
            <w:XmlFragment xmlns:w="urn:x"><LastBootUpTime><Datetime>2026-10-29T07:19:44.5+01:00</Datetime></LastBootUpTime></w:XmlFragment>
            """);

        var obj = WsmanObjectReader.Read(fragment, null, null, null, null, "Win32_OperatingSystem");

        Assert.Equal(new DateTime(2026, 10, 29, 6, 19, 44, 500, DateTimeKind.Utc), obj["LastBootUpTime"]);
    }

    [Fact]
    public void An_embedded_instance_whose_only_property_is_Interval_stays_an_instance()
    {
        var instance = XElement.Parse("""
            <p:Thing xmlns:p="urn:x" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <p:Pattern xsi:type="p:Schedule_Type"><p:Interval>PT5M</p:Interval></p:Pattern>
              <p:Other><Datetime>x</Datetime></p:Other>
            </p:Thing>
            """);

        var obj = WsmanObjectReader.Read(instance, null, [], null, null);

        Assert.Equal("PT5M", Assert.IsType<WmiInstance>(obj["Pattern"])["Interval"]);
    }

    [Fact]
    public void Without_a_schema_repeated_references_and_datetimes_are_typed_arrays()
    {
        const string reference = """
            <p:Owners><a:Address>x</a:Address><a:ReferenceParameters>
              <w:ResourceURI>http://schemas.microsoft.com/wbem/wsman/1/wmi/root/cimv2/Win32_Account</w:ResourceURI>
              <w:SelectorSet><w:Selector Name="Name">{0}</w:Selector></w:SelectorSet>
            </a:ReferenceParameters></p:Owners>
            """;
        var instance = XElement.Parse($"""
            <p:Thing xmlns:p="urn:x" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd" xmlns:cim="http://schemas.dmtf.org/wbem/wscim/1/common">
              {string.Format(CultureInfo.InvariantCulture, reference, "a")}{string.Format(CultureInfo.InvariantCulture, reference, "b")}
              <p:Times><cim:Datetime>2026-01-02T03:04:05Z</cim:Datetime></p:Times><p:Times><cim:Datetime>2026-01-03T03:04:05Z</cim:Datetime></p:Times>
            </p:Thing>
            """);

        var obj = WsmanObjectReader.Read(instance, null, [], null, null);

        Assert.Equal(WmiType.ReferenceArray, obj.Properties["Owners"]!.Type);
        Assert.Equal(["a", "b"], Assert.IsType<WmiInstance[]>(obj["Owners"]).Select(o => o["Name"]));
        Assert.Equal(WmiType.DateTimeArray, obj.Properties["Times"]!.Type);
        Assert.IsType<DateTime[]>(obj["Times"]);
    }

    [Fact]
    public void An_interval_longer_than_a_TimeSpan_stays_text() =>
        Assert.Equal("P99999999DT23H59M59.999999S", WsmanObjectReader.ParseInterval("P99999999DT23H59M59.999999S"));

    [Fact]
    public void A_datetime_that_does_not_parse_stays_text() =>
        Assert.Equal("not a date", WsmanObjectReader.ParseDatetime("not a date"));

    [Fact]
    public void Intervals_become_TimeSpans() =>
        Assert.Equal(new TimeSpan(1, 2, 3, 4, 500), WsmanObjectReader.ParseInterval("P1DT2H3M4.5S"));

    [Fact]
    public void An_interval_that_does_not_parse_stays_text() =>
        Assert.Equal("1 day", WsmanObjectReader.ParseInterval("1 day"));

    private const string ClassXml = """
        <CLASS NAME="Win32_Thing">
          <PROPERTY NAME="Handle" TYPE="uint32"/>
          <PROPERTY NAME="Name" TYPE="string"/>
          <PROPERTY NAME="Enabled" TYPE="boolean"/>
          <PROPERTY.ARRAY NAME="Tags" TYPE="string"/>
          <PROPERTY.ARRAY NAME="Sizes" TYPE="uint16"/>
          <METHOD NAME="Run" TYPE="uint32"><PARAMETER NAME="ProcessId" TYPE="uint32"/></METHOD>
        </CLASS>
        """;

    [Fact]
    public void A_class_definition_types_the_values_and_keeps_single_element_arrays()
    {
        var schema = WsmanClassSchema.Parse(XElement.Parse(ClassXml));
        var instance = XElement.Parse("""
            <p:Win32_Thing xmlns:p="urn:x" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <p:Handle>4</p:Handle><p:Name>true</p:Name><p:Enabled>false</p:Enabled>
              <p:Tags>only</p:Tags><p:Sizes>1</p:Sizes>
            </p:Win32_Thing>
            """);

        var obj = WsmanObjectReader.Read(instance, null, [], "h", "ns", null, name => name == "Win32_Thing" ? schema : null);

        Assert.Equal(4u, obj["Handle"]);
        Assert.Equal("true", obj["Name"]);
        Assert.Equal(false, obj["Enabled"]);
        Assert.Equal(new[] { "only" }, Assert.IsType<string[]>(obj["Tags"]));
        Assert.Equal(new ushort[] { 1 }, Assert.IsType<ushort[]>(obj["Sizes"]));
    }

    [Fact]
    public void A_method_schema_types_the_return_value_and_out_parameters()
    {
        var method = WsmanClassSchema.Parse(XElement.Parse(ClassXml)).Method("Run")!;
        var output = XElement.Parse("<p:Run_OUTPUT xmlns:p=\"urn:x\"><p:ReturnValue>0</p:ReturnValue><p:ProcessId>12</p:ProcessId></p:Run_OUTPUT>");

        var obj = WsmanObjectReader.Read(output, null, [], "h", "ns", null, _ => method);

        Assert.Equal(0u, obj["ReturnValue"]);
        Assert.Equal(12u, obj["ProcessId"]);
    }
}
