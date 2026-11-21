using NWmi.Wmi;

namespace NWmi.Tests.Wmi;

public class CimObjectPathFormatTests
{
    [Fact]
    public void NamespacePath_without_a_server_is_just_the_namespace()
    {
        Assert.Equal(@"root\cimv2", new CimObjectPath("Win32_Process", @namespace: @"root\cimv2").NamespacePath);
    }

    [Fact]
    public void NamespacePath_with_a_server_adds_the_UNC_prefix()
    {
        var path = new CimObjectPath("Win32_Process", @namespace: @"root\cimv2", server: "server01");
        Assert.Equal(@"\\server01\root\cimv2", path.NamespacePath);
    }

    [Fact]
    public void NamespacePath_is_null_without_a_namespace()
    {
        Assert.Null(new CimObjectPath("Win32_Process").NamespacePath);
    }

    [Fact]
    public void RelativePath_formats_each_typed_constant()
    {
        var path = new CimObjectPath("Win32_Process", new Dictionary<string, object>
        {
            ["Handle"] = 4,
            ["Name"] = "notepad.exe",
            ["IsRunning"] = true,
        });

        Assert.Equal("Win32_Process.Handle=4,Name=\"notepad.exe\",IsRunning=TRUE", path.RelativePath);
    }

    [Fact]
    public void RelativePath_escapes_quotes_and_backslashes_in_strings()
    {
        var path = new CimObjectPath("Win32_LogicalDisk", new Dictionary<string, object> { ["DeviceID"] = "C:\\" });
        Assert.Equal("Win32_LogicalDisk.DeviceID=\"C:\\\\\"", path.RelativePath);
    }

    [Fact]
    public void RelativePath_without_keys_is_the_class_name_and_a_singleton_ends_in_at()
    {
        Assert.Equal("Win32_Process", new CimObjectPath("Win32_Process").RelativePath);
        Assert.Equal("Win32_WmiSetting=@", new CimObjectPath("Win32_WmiSetting", isSingleton: true).RelativePath);
    }

    [Fact]
    public void RelativePath_rejects_a_key_value_of_an_unsupported_type()
    {
        var path = new CimObjectPath("Thing", new Dictionary<string, object> { ["When"] = DateTime.Now });
        Assert.Throws<ArgumentException>(() => path.RelativePath);
    }

    [Fact]
    public void ToString_joins_namespace_and_relative_path_with_a_colon()
    {
        var path = new CimObjectPath("Win32_Process", new Dictionary<string, object> { ["Handle"] = 4 }, @"root\cimv2");
        Assert.Equal(@"root\cimv2:Win32_Process.Handle=4", path.ToString());
        Assert.Equal("Win32_Process.Handle=4",
            new CimObjectPath("Win32_Process", new Dictionary<string, object> { ["Handle"] = 4 }).ToString());
    }
}
