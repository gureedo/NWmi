using System.Xml.Linq;
using NWmi.WinRm;
using NWmi.Wmio;

namespace NWmi.Tests.Client;

/// <summary>The .NET types of property values: datetimes, references and arrays, read and written.</summary>
public class WmiValueTests
{
    private const string AccountPath = @"\\.\root\cimv2:Win32_Account.Domain=""CONTOSO"",Name=""Administrator""";

    private static WmioProperty Property(string name, WbemType type, ushort index, params WmioQualifier[] qualifiers) =>
        new(name, type, false, index, 0, qualifiers, new NullAndDefaultFlag(true, false), null, 0);

    private static WmioQualifier Key() => new("key", QualifierFlavor.NotOverridable, WbemType.Boolean, true);

    private static WmioClass ValuesClass() => new("NWmi_Values", [], [],
    [
        Property("Id", WbemType.String, 0, Key()),
        Property("When", WbemType.DateTime, 1),
        Property("Span", WbemType.DateTime, 2),
        Property("Wild", WbemType.DateTime, 3),
        Property("Owner", WbemType.Reference, 4),
        Property("Sizes", WbemType.Uint32.AsArray(), 5),
        Property("Names", WbemType.String.AsArray(), 6),
        Property("Data", WbemType.Uint8.AsArray(), 7),
        Property("Times", WbemType.DateTime.AsArray(), 8),
        Property("Mixed", WbemType.DateTime.AsArray(), 9),
        Property("Owners", WbemType.Reference.AsArray(), 10),
        Property("Empty", WbemType.Sint32.AsArray(), 11),
    ], []);

    /// <summary>An instance as the server sends it: through the binary encoding and back.</summary>
    private static WmiInstance Read(IReadOnlyDictionary<string, object?> values)
    {
        var block = WmioObjectBlock.ForInstance(WmioInstance.Create(ValuesClass(), values));
        return WmiInstance.FromWire(WmioObjectBlock.DecodeEncodingUnit(block.EncodeEncodingUnit()), "host",
            @"root\cimv2");
    }

    private static WmiInstance ReadAll() => Read(new Dictionary<string, object?>
    {
        ["Id"] = "1",
        ["When"] = "20261121093000.000000+060",
        ["Span"] = "00000001020304.500000:000",
        ["Wild"] = "2026****093000.000000+060",
        ["Owner"] = AccountPath,
        ["Sizes"] = new object?[] { 1u, 2u },
        ["Names"] = new object?[] { "a", "b" },
        ["Data"] = new object?[] { (byte)1, (byte)255 },
        ["Times"] = new object?[] { "20260101000000.000000+000", "20260102000000.000000+000" },
        ["Mixed"] = new object?[] { "20260101000000.000000+000", "00000000000001.000000:000" },
        ["Owners"] = new object?[] { AccountPath },
        ["Empty"] = Array.Empty<object?>(),
    });

    private static readonly DateTime Utc = new(2026, 11, 21, 8, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void A_timestamp_reads_as_a_UTC_DateTime()
    {
        var value = Assert.IsType<DateTime>(ReadAll()["When"]);

        Assert.Equal(Utc, value);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Fact]
    public void An_interval_reads_as_a_TimeSpan() =>
        Assert.Equal(new TimeSpan(1, 2, 3, 4, 500), ReadAll()["Span"]);

    [Fact]
    public void A_datetime_with_wildcards_stays_a_DMTF_string() =>
        Assert.Equal("2026****093000.000000+060", ReadAll()["Wild"]);

    [Fact]
    public void Arrays_read_as_arrays_of_their_element_type()
    {
        var obj = ReadAll();

        Assert.Equal(new uint[] { 1, 2 }, Assert.IsType<uint[]>(obj["Sizes"]));
        Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(obj["Names"]));
        Assert.Equal(new byte[] { 1, 255 }, Assert.IsType<byte[]>(obj["Data"]));
        Assert.Equal(
            new[] { new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
            Assert.IsType<DateTime[]>(obj["Times"]));
        Assert.Empty(Assert.IsType<int[]>(obj["Empty"]));
        Assert.Equal(AccountPath, Assert.Single(Assert.IsType<WmiInstance[]>(obj["Owners"])).SystemProperties.Path);
    }

    [Fact]
    public void An_array_of_timestamps_and_intervals_reads_as_an_object_array()
    {
        var mixed = Assert.IsType<object?[]>(ReadAll()["Mixed"]);

        Assert.IsType<DateTime>(mixed[0]);
        Assert.Equal(TimeSpan.FromSeconds(1), mixed[1]);
    }

    [Fact]
    public void A_reference_reads_as_an_instance_holding_the_keys_of_the_one_it_refers_to()
    {
        var owner = Assert.IsType<WmiInstance>(ReadAll()["Owner"]);

        Assert.True(owner.IsReference);
        Assert.Equal("Win32_Account", owner.SystemProperties.ClassName);
        Assert.Equal(@"root\cimv2", owner.SystemProperties.Namespace);
        Assert.Equal(".", owner.SystemProperties.Server);
        Assert.Equal(AccountPath, owner.SystemProperties.Path);
        Assert.Equal(["Domain", "Name"], owner.Properties.Select(p => p.Name));
        Assert.Equal("Administrator", owner["Name"]);
        Assert.Equal(WmiType.String, owner.Properties["Name"]!.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.NotModified, owner.Properties["Name"]!.Flags);
        Assert.Equal(["Domain", "Name"], owner.Class.Properties.Select(p => p.Name));
    }

    [Fact]
    public void A_path_that_does_not_parse_stays_a_string() =>
        Assert.Equal("not a path!", Read(new Dictionary<string, object?> { ["Id"] = "1", ["Owner"] = "not a path!" })["Owner"]);

    [Fact]
    public void A_singleton_reference_keeps_its_path()
    {
        const string path = @"\\.\root\cimv2:Win32_WMISetting=@";
        var owner = (WmiInstance)Read(new Dictionary<string, object?> { ["Id"] = "1", ["Owner"] = path })["Owner"]!;

        Assert.Empty(owner.Properties);
        Assert.Equal(path, owner.SystemProperties.Path);
    }

    [Fact]
    public void Read_values_are_written_back_as_they_came()
    {
        var sent = ReadAll().ToInstanceBlock().Instance!.Values;

        Assert.Equal("20261121083000.000000+000", sent["When"]); // the same instant, in UTC
        Assert.Equal("00000001020304.500000:000", sent["Span"]);
        Assert.Equal("2026****093000.000000+060", sent["Wild"]);
        Assert.Equal(AccountPath, sent["Owner"]);
        Assert.Equal(new object?[] { 1u, 2u }, sent["Sizes"]);
        Assert.Equal(new object?[] { (byte)1, (byte)255 }, sent["Data"]);
        Assert.Equal(new object?[] { "20260101000000.000000+000", "20260102000000.000000+000" }, sent["Times"]);
        Assert.Equal(new object?[] { AccountPath }, sent["Owners"]);
    }

    [Fact]
    public void A_reference_can_be_set_as_a_path_string_or_any_instance()
    {
        var obj = ReadAll();

        obj["Owner"] = @"\\.\root\cimv2:Win32_Account.Domain=""X"",Name=""Y""";
        var owner = Assert.IsType<WmiInstance>(obj["Owner"]);
        Assert.Equal("Y", owner["Name"]);

        // An instance that was not read from a reference goes as its namespace and relative path.
        var account = new WmiInstance("Win32_UserAccount", @"root\cimv2");
        account.Properties.Add(WmiProperty.Create("Domain", "D", WmiFlags.Key));
        account.Properties.Add(WmiProperty.Create("Name", "N", WmiFlags.Key));
        obj["Owner"] = account;
        Assert.Equal(@"root\cimv2:Win32_UserAccount.Domain=""D"",Name=""N""",
            obj.ToInstanceBlock().Instance!.Values["Owner"]);
    }

    [Fact]
    public void A_datetime_array_can_be_set_as_any_sequence_of_accepted_forms()
    {
        var obj = ReadAll();

        obj["Times"] = new List<DateTimeOffset> { new(2026, 11, 21, 9, 30, 0, TimeSpan.FromHours(1)) };

        Assert.Equal(new[] { Utc }, Assert.IsType<DateTime[]>(obj["Times"]));
    }

    [Fact]
    public void A_key_that_is_a_reference_goes_into_the_path_as_the_referenced_path()
    {
        var @class = new WmioClass("Win32_LoggedOnUser", [], [],
        [
            Property("Antecedent", WbemType.Reference, 0, Key()),
            Property("Dependent", WbemType.Reference, 1, Key()),
        ], []);
        var block = WmioObjectBlock.ForInstance(WmioInstance.Create(@class, new Dictionary<string, object?>
        {
            ["Antecedent"] = AccountPath,
            ["Dependent"] = @"\\.\root\cimv2:Win32_LogonSession.LogonId=""999""",
        }));

        var logon = WmiInstance.FromWire(block, "host", @"root\cimv2");

        Assert.Equal(
            @"Win32_LoggedOnUser.Antecedent=""\\\\.\\root\\cimv2:Win32_Account.Domain=\""CONTOSO\"",Name=\""Administrator\"""",Dependent=""\\\\.\\root\\cimv2:Win32_LogonSession.LogonId=\""999\""""",
            logon.SystemProperties.RelativePath);
    }

    [Fact]
    public void A_copy_does_not_share_arrays()
    {
        var obj = ReadAll();
        var copy = new WmiInstance(obj);

        ((uint[])copy["Sizes"]!)[0] = 99;
        ((WmiInstance[])copy["Owners"]!)[0]["Name"] = "Other";

        Assert.Equal(1u, ((uint[])obj["Sizes"]!)[0]);
        Assert.Equal("Administrator", ((WmiInstance[])obj["Owners"]!)[0]["Name"]);
    }

    [Fact]
    public void A_copy_keeps_a_string_array_a_string_array()
    {
        var copy = new WmiInstance(ReadAll());

        Assert.IsType<string[]>(copy["Names"]);
    }

    [Theory]
    [InlineData("1", WmiType.UInt32, 1u)]
    [InlineData(1L, WmiType.UInt32, 1u)]
    [InlineData(1L, WmiType.String, "1")]
    [InlineData("true", WmiType.Boolean, true)]
    [InlineData("-3", WmiType.SInt16, (short)-3)]
    public void A_reference_key_converts_to_its_declared_type(object value, WmiType type, object expected)
    {
        Assert.True(WmiValues.TryToKeyType(value, type, out var converted));
        Assert.Equal(expected, converted);
    }

    [Fact]
    public void A_reference_key_that_does_not_convert_is_left_alone()
    {
        Assert.False(WmiValues.TryToKeyType("x", WmiType.UInt32, out var converted));
        Assert.Equal("x", converted);
        Assert.False(WmiValues.TryToKeyType(-1L, WmiType.UInt8, out _));
    }

    [Fact]
    public void A_datetime_key_converts_from_its_DMTF_text()
    {
        Assert.True(WmiValues.TryToKeyType("20261121083000.000000+000", WmiType.DateTime, out var converted));
        Assert.Equal(Utc, converted);
    }

    [Fact]
    public void Over_WinRM_a_datetime_key_is_named_by_its_DMTF_string()
    {
        var instance = new WmiInstance("Test", @"root\cimv2");
        instance.Properties.Add(WmiProperty.Create("When", Utc, WmiFlags.Key));
        instance.Properties.Add(WmiProperty.Create("Owner", ReadAll()["Owner"], WmiFlags.Key));

        var selectors = WinRmBackend.KeySelectors(instance);

        Assert.Equal("20261121083000.000000+000", selectors.Single(s => s.Key == "When").Value);
        Assert.Equal(AccountPath, selectors.Single(s => s.Key == "Owner").Value);
    }

    [Fact]
    public void A_declared_default_value_has_the_same_types()
    {
        var @class = new WmioClass("NWmi_Defaults", [], [],
        [
            new WmioProperty("Sizes", WbemType.Uint16.AsArray(), false, 0, 0, [], new NullAndDefaultFlag(false, false),
                new object?[] { (ushort)1 }, 0),
            new WmioProperty("When", WbemType.DateTime, false, 1, 0, [], new NullAndDefaultFlag(false, false),
                "20261121093000.000000+060", 0),
        ], []);
        var declared = WmiClass.FromWire(@class, null, null);

        Assert.Equal(new ushort[] { 1 }, Assert.IsType<ushort[]>(declared.Properties["Sizes"]!.Value));
        Assert.Equal(Utc, declared.Properties["When"]!.Value);
        Assert.Equal(Utc, new WmiInstance(declared)["When"]);
    }

    [Fact]
    public void A_client_built_property_takes_its_type_from_a_DateTime_or_a_reference()
    {
        var owner = (WmiInstance)ReadAll()["Owner"]!;

        Assert.Equal(WmiType.DateTime, WmiProperty.Create("When", Utc, WmiFlags.None).Type);
        Assert.Equal(WmiType.Reference, WmiProperty.Create("Owner", owner, WmiFlags.None).Type);
        Assert.Equal(WmiType.ReferenceArray, WmiProperty.Create("Owners", new[] { owner }, WmiFlags.None).Type);
        Assert.Equal(WmiType.Instance, WmiProperty.Create("Child", new WmiInstance("X"), WmiFlags.None).Type);
    }

    [Fact]
    public void An_untyped_method_parameter_sends_a_DateTime_and_a_reference_with_their_types()
    {
        var owner = (WmiInstance)ReadAll()["Owner"]!;

        var block = WmiConnection.BuildParametersBlock(new Dictionary<string, object?> { ["When"] = Utc, ["Who"] = owner });

        var when = block.Class!.FindProperty("When")!;
        Assert.Equal(WbemType.DateTime, when.Type);
        Assert.Equal("20261121083000.000000+000", when.DefaultValue);
        var who = block.Class.FindProperty("Who")!;
        Assert.Equal(WbemType.Reference, who.Type);
        Assert.Equal(AccountPath, who.DefaultValue);
        Assert.Equal("ref:Win32_Account", who.Qualifiers.Single(q => q.Name == "CIMTYPE").Value);
    }

    [Fact]
    public void Over_WinRM_a_DateTime_goes_as_a_cim_Datetime_in_UTC()
    {
        var parent = new XElement("parent");
        WinRmBackend.AppendValue(parent, "Value", Utc.ToLocalTime());

        var value = parent.Elements().Single().Elements().Single();
        Assert.Equal(Wsman.Cim + "Datetime", value.Name);
        Assert.Equal("2026-11-21T08:30:00.000000+00:00", value.Value);
    }

    [Fact]
    public void Over_WinRM_a_reference_goes_as_its_path()
    {
        var owner = (WmiInstance)ReadAll()["Owner"]!;
        var parent = new XElement("parent");

        WinRmBackend.AppendValue(parent, "Owner", owner);

        Assert.Equal(AccountPath, parent.Elements().Single().Value);
    }

    [Fact]
    public void Over_WinRM_an_instance_set_on_a_reference_property_goes_as_its_path()
    {
        var schema = WsmanClassSchema.Parse(XElement.Parse("""
            <CLASS NAME="Test"><PROPERTY.REFERENCE NAME="Owner" REFERENCECLASS="Win32_Account"/></CLASS>
            """));
        var account = new WmiInstance("Win32_Account", @"root\cimv2");
        account.Properties.Add(WmiProperty.Create("Name", "N", WmiFlags.Key));

        Assert.Equal(@"root\cimv2:Win32_Account.Name=""N""", WinRmBackend.ForWire(account, "Owner", schema));
    }
}
