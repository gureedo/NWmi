namespace NWmi.Tests.Wmi;

public class WmiObjectPathParseTests
{
    [Fact]
    public void Parse_reads_a_bare_class_name()
    {
        var path = WmiObjectPath.Parse("Win32_Process");

        Assert.Equal("Win32_Process", path.ClassName);
        Assert.True(path.IsClass);
        Assert.False(path.IsInstance);
        Assert.Empty(path.Keys);
        Assert.Null(path.Namespace);
        Assert.Null(path.Server);
    }

    [Fact]
    public void Parse_reads_each_kind_of_typed_constant()
    {
        var path = WmiObjectPath.Parse("""Thing.S="a b",I=-42,U=18446744073709551615,R=1.5,T=TRUE,F=false,D=20260101120000.000000+000""");

        Assert.Equal(
            new KeyValuePair<string, object>[]
            {
                new("S", "a b"), new("I", -42L), new("U", ulong.MaxValue), new("R", 1.5), new("T", true), new("F", false),
                new("D", "20260101120000.000000+000"),
            },
            path.Keys);
        Assert.True(path.IsInstance);
    }

    [Fact]
    public void Parse_unescapes_strings_and_keeps_colons_commas_and_equals_signs_inside_them()
    {
        var path = WmiObjectPath.Parse("Win32_LogicalDisk.DeviceID=\"C:\\\\Dir\\\"x\\\", y=z\",Other='it\\'s'");

        Assert.Equal("""C:\Dir"x", y=z""", path["DeviceID"]);
        Assert.Equal("it's", path["Other"]);
        Assert.Null(path.Namespace); // the colon inside the string is not a namespace separator
    }

    [Theory]
    [InlineData("\\\\server01\\root\\cimv2:Win32_Process.Handle=\"4\"", "server01", @"root\cimv2")]
    [InlineData("//server01/root/cimv2:Win32_Process.Handle=\"4\"", "server01", @"root\cimv2")]
    [InlineData("\\\\.\\root\\default:Win32_Process.Handle=\"4\"", ".", @"root\default")]
    public void Parse_reads_the_machine_and_namespace_with_either_slash(string text, string server, string @namespace)
    {
        var path = WmiObjectPath.Parse(text);

        Assert.Equal(server, path.Server);
        Assert.Equal(@namespace, path.Namespace);
        Assert.Equal("Win32_Process", path.ClassName);
        Assert.Equal("4", path["Handle"]);
    }

    [Fact]
    public void Parse_reads_a_namespace_without_a_machine_and_a_hyphenated_namespace()
    {
        var path = WmiObjectPath.Parse(@"root\Microsoft\Windows-Storage:MSFT_Disk.Number=1");

        Assert.Null(path.Server);
        Assert.Equal(@"root\Microsoft\Windows-Storage", path.Namespace);
        Assert.Equal(1L, path["Number"]);
    }

    [Fact]
    public void Parse_reads_a_singleton()
    {
        var path = WmiObjectPath.Parse(@"root\cimv2:Win32_WmiSetting=@");

        Assert.True(path.IsSingleton);
        Assert.True(path.IsInstance);
        Assert.Empty(path.Keys);
        Assert.Equal("Win32_WmiSetting=@", path.RelativePath);
    }

    [Fact]
    public void Parse_tolerates_whitespace_and_key_lookup_ignores_case()
    {
        var path = WmiObjectPath.Parse("""  Thing.A = 1 , b = "x"  """);

        Assert.Equal(1L, path["a"]);
        Assert.Equal("x", path["B"]);
        Assert.True(path.TryGetKey("b", out _));
        Assert.False(path.TryGetKey("c", out _));
        Assert.Throws<KeyNotFoundException>(() => path["c"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\root\cimv2")] // a namespace, not an object
    [InlineData(@"\\server:Thing")] // a machine path needs a namespace
    [InlineData(@"root\cimv2:")] // no class
    [InlineData("9Thing")]
    [InlineData("Thing.")]
    [InlineData("Thing.A")]
    [InlineData("Thing.A=")]
    [InlineData("Thing.A=1,")]
    [InlineData("Thing.A=1 B=2")]
    [InlineData("""Thing.A="open""")]
    [InlineData("Thing.A=1,A=2")] // duplicate key
    [InlineData("Thing.A=abc")] // not a typed constant
    [InlineData("Thing.A=99999999999999999999")] // too big
    [InlineData("Thing=\"value\"")] // a key without a name
    [InlineData("Thing=@x")]
    [InlineData("Thing.=1")]
    public void Parse_refuses_what_is_not_an_object_path(string text)
    {
        Assert.Throws<FormatException>(() => WmiObjectPath.Parse(text));
        Assert.False(WmiObjectPath.TryParse(text, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Win32_Process")]
    [InlineData("Win32_Process.Handle=\"4\"")]
    [InlineData("root\\cimv2:Win32_LogicalDisk.DeviceID=\"C:\\\\Temp\\\"q\\\"\"")]
    [InlineData("\\\\server01\\root\\cimv2:Thing.A=1,B=TRUE,C=-2.5,D=\"x,y\"")]
    [InlineData(@"\\.\root\cimv2:Win32_WmiSetting=@")]
    public void A_parsed_path_prints_back_to_the_same_text(string text)
    {
        var path = WmiObjectPath.Parse(text);

        Assert.Equal(text, path.ToString());
        Assert.Equal(path.ToString(), WmiObjectPath.Parse(path.ToString()).ToString());
    }

    [Fact]
    public void A_path_can_be_built_and_printed()
    {
        var path = new WmiObjectPath("Win32_Process", [new KeyValuePair<string, object>("Handle", "4")], @"root\cimv2", "server01");

        Assert.Equal("\\\\server01\\root\\cimv2:Win32_Process.Handle=\"4\"", path.ToString());
        Assert.Equal(@"\\server01\root\cimv2", path.NamespacePath);
        Assert.Equal("Win32_Process.Handle=\"4\"", path.RelativePath);
        Assert.Throws<ArgumentException>(() => new WmiObjectPath("X", [new KeyValuePair<string, object>("A", 1)], isSingleton: true));
        Assert.Throws<ArgumentException>(() => new WmiObjectPath("X", server: "s"));
        Assert.Throws<ArgumentException>(() => new WmiObjectPath(""));
    }

    [Fact]
    public void A_relative_path_parses_back_to_its_keys()
    {
        var text = new WmiObjectPath("Thing", new Dictionary<string, object>
        {
            ["Name"] = """a"b\c""", ["N"] = 7, ["Flag"] = true,
        }).RelativePath;

        var path = WmiObjectPath.Parse(text);

        Assert.Equal("""a"b\c""", path["Name"]);
        Assert.Equal(7L, path["N"]);
        Assert.Equal(true, path["Flag"]);
    }
}
