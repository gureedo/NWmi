using System.Globalization;
using NWmi.Auth;
using NWmi.Client;
using NWmi.Wmi;

namespace NWmi.IntegrationTests;

public class ArrayAndPathTests
{
    private const uint HklmKey = 0x80000002;
    private const string TestKey = @"SOFTWARE\NWmiArrayTest";

    private static Task<WmiConnection> ConnectAsync(string ns = @"root\cimv2") =>
        WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = ns });

    private static async Task<WmiInstance> RegAsync(WmiConnection wmi, string method, Dictionary<string, object?> parameters)
    {
        var result = await wmi.InvokeMethodAsync("StdRegProv", method, parameters);
        Assert.NotNull(result);
        Assert.Equal(0, Convert.ToInt32(result["ReturnValue"], CultureInfo.InvariantCulture));
        return result;
    }

    private static Dictionary<string, object?> Value(string name, string? parameter = null, object? value = null)
    {
        var parameters = new Dictionary<string, object?> { ["hDefKey"] = HklmKey, ["sSubKeyName"] = TestKey, ["sValueName"] = name };
        if (parameter is not null)
            parameters[parameter] = value;

        return parameters;
    }

    [IntegrationFact]
    public async Task Array_parameters_go_in_and_come_back_through_StdRegProv()
    {
        await using var wmi = await ConnectAsync(@"root\default");
        var key = new Dictionary<string, object?> { ["hDefKey"] = HklmKey, ["sSubKeyName"] = TestKey };
        try
        {
            await RegAsync(wmi, "CreateKey", key);

            string[] lines = ["first", "second line", "ünïcode ✓"];
            await RegAsync(wmi, "SetMultiStringValue", Value("Lines", "sValue", lines));
            var multi = await RegAsync(wmi, "GetMultiStringValue", Value("Lines"));
            Assert.Equal(lines, ((object?[])multi["sValue"]!).Cast<string>());

            byte[] data = [0, 1, 2, 127, 128, 255];
            await RegAsync(wmi, "SetBinaryValue", Value("Blob", "uValue", data));
            var binary = await RegAsync(wmi, "GetBinaryValue", Value("Blob"));
            Assert.Equal(data, ((object?[])binary["uValue"]!).Cast<byte>());

            // A list of ints for a uint8 array, and an empty array.
            await RegAsync(wmi, "SetBinaryValue", Value("Ints", "uValue", new List<int> { 9, 8, 7 }));
            await RegAsync(wmi, "SetMultiStringValue", Value("Empty", "sValue", Array.Empty<string>()));

            var names = await RegAsync(wmi, "EnumValues", key);
            Assert.Equal(["Blob", "Empty", "Ints", "Lines"], ((object?[])names["sNames"]!).Cast<string>().Order(StringComparer.OrdinalIgnoreCase));
            var ints = await RegAsync(wmi, "GetBinaryValue", Value("Ints"));
            Assert.Equal(new byte[] { 9, 8, 7 }, ((object?[])ints["uValue"]!).Cast<byte>());
        }
        finally
        {
            await wmi.InvokeMethodAsync("StdRegProv", "DeleteKey", key);
        }
    }

    [IntegrationFact]
    public async Task Paths_the_server_returns_parse_and_name_the_same_instance()
    {
        await using var wmi = await ConnectAsync();
        var disks = new List<WmiInstance>();
        await foreach (var disk in wmi.QueryAsync("SELECT DeviceID FROM Win32_LogicalDisk"))
            disks.Add(disk);

        Assert.NotEmpty(disks);
        foreach (var disk in disks)
        {
            var path = CimObjectPath.Parse(disk.SystemProperties.Path!);
            Assert.Equal("Win32_LogicalDisk", path.ClassName);
            Assert.True(path.IsInstance);
            Assert.Equal(disk["DeviceID"], path["DeviceID"]);
            Assert.Equal(@"root\cimv2", path.Namespace, ignoreCase: true);

            // The parsed path addresses the very same instance.
            var again = await wmi.GetInstanceAsync(path.RelativePath);
            Assert.Equal(disk["DeviceID"], again!["DeviceID"]);
        }

        // A full path with a server and namespace works as well as a relative one.
        Assert.NotNull(await wmi.GetInstanceAsync(disks[0].SystemProperties.Path!));
        Assert.NotNull(await wmi.GetInstanceAsync(disks[0].SystemProperties.RelativePath!));
    }
}
