using System.Globalization;
using NWmi.Auth;
using NWmi.Client;

namespace NWmi.IntegrationTests;

public class RefresherTests
{
    private static Task<WmiConnection> ConnectAsync() =>
        WmiConnection.ConnectAsync(
            IntegrationSettings.Host!, Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!));

    [IntegrationFact]
    public async Task A_performance_object_and_enumeration_refresh_in_one_call()
    {
        await using var wmi = await ConnectAsync();
        await using var refresher = await wmi.CreateRefresherAsync();
        var memory = await refresher.AddObjectAsync("Win32_PerfRawData_PerfOS_Memory=@");
        var processes = await refresher.AddEnumerationAsync("Win32_PerfRawData_PerfProc_Process");
        Assert.True(memory.IsHighPerformance);
        Assert.True(processes.IsHighPerformance);

        await refresher.RefreshAsync();
        await refresher.RefreshAsync();

        Assert.NotNull(memory.Instance);
        Assert.True(Convert.ToUInt64(memory.Instance["AvailableBytes"], CultureInfo.InvariantCulture) > 0);
        Assert.Contains(processes.Objects, p => (string?)p["Name"] == "Idle");
        Assert.Equal("Win32_PerfRawData_PerfProc_Process", processes.Objects[0].SystemProperties.ClassName);

        await refresher.RemoveAsync(processes);
        await refresher.RefreshAsync();
    }

    [IntegrationFact]
    public async Task An_ordinary_class_is_re_read_on_each_refresh()
    {
        await using var wmi = await ConnectAsync();
        await using var refresher = await wmi.CreateRefresherAsync();
        var os = await refresher.AddObjectAsync("Win32_OperatingSystem=@");
        Assert.False(os.IsHighPerformance);

        await refresher.RefreshAsync();
        Assert.NotNull(os.Instance);
        Assert.False(string.IsNullOrEmpty((string?)os.Instance["Caption"]));
    }
}
