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

        Assert.Equal("Win32_Thing", obj.ClassName);
        Assert.Equal("4", obj["Handle"]);
        Assert.Equal(true, obj["Enabled"]);
        Assert.Null(obj["Missing"]);
        Assert.Equal("20261029071949.637856+060", obj["Created"]);
        Assert.Equal(new object?[] { "a", "b" }, (object?[])obj["Tags"]!);
        Assert.Equal("7", ((WmiObject)obj["Child"]!)["Size"]);
        Assert.EndsWith("Win32_Thing.Handle=\"4\"", obj.Path);
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
        Assert.Equal(["Handle"], obj.KeyPropertyNames);
    }

    [Fact]
    public void A_bare_fragment_takes_its_class_from_the_query()
    {
        var fragment = XElement.Parse("<w:XmlFragment xmlns:w=\"urn:x\"><Name>n</Name></w:XmlFragment>");
        var obj = WsmanObjectReader.Read(fragment, null, null, null, null,
            WsmanObjectReader.ClassOfQuery("select Name from Win32_Service where x = 1"));
        Assert.Equal("Win32_Service", obj.ClassName);
    }

    [Theory]
    [InlineData("2026-01-02T03:04:05+00:00", "20260102030405.000000+000")]
    [InlineData("2026-01-02T03:04:05.5-05:30", "20260102030405.500000-330")]
    public void Datetimes_become_DMTF_strings(string iso, string expected) =>
        Assert.Equal(expected, WsmanObjectReader.ToDmtfDateTime(iso));

    [Fact]
    public void Intervals_become_DMTF_intervals() =>
        Assert.Equal("00000001020304.500000:000", WsmanObjectReader.ToDmtfInterval("P1DT2H3M4.5S"));

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
        Assert.Equal(new object?[] { "only" }, (object?[])obj["Tags"]!);
        Assert.Equal(new object?[] { (ushort)1 }, (object?[])obj["Sizes"]!);
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
