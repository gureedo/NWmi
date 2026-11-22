using System.Globalization;
using NWmi.Options;

namespace NWmi.IntegrationTests;

public class EmbeddedObjectTests
{
    private const uint IdlePriorityClass = 0x40;

    [IntegrationFact]
    public async Task An_embedded_object_parameter_reaches_the_provider()
    {
        await using var wmi = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!, Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!));

        // Win32_Process.Create(CommandLine, CurrentDirectory, ProcessStartupInformation): the last one is an object.
        var startup = new WmiInstance(await wmi.GetClassAsync("Win32_ProcessStartup"));
        Assert.NotNull(startup);
        startup["PriorityClass"] = IdlePriorityClass;
        startup["ShowWindow"] = (ushort)0;

        var created = await wmi.InvokeMethodAsync(
            "Win32_Process",
            "Create",
            new Dictionary<string, object?>
            {
                ["CommandLine"] = "ping -n 30 127.0.0.1",
                ["ProcessStartupInformation"] = startup,
            });
        Assert.NotNull(created);
        Assert.Equal(0, Convert.ToInt32(created["ReturnValue"], CultureInfo.InvariantCulture));
        var processId = Convert.ToUInt32(created["ProcessId"], CultureInfo.InvariantCulture);
        try
        {
            // The provider applied the startup information: an idle-priority class process has base priority 4.
            uint? priority = null;
            await foreach (var row in wmi.QueryAsync($"SELECT Priority FROM Win32_Process WHERE ProcessId = {processId}"))
                priority = Convert.ToUInt32(row["Priority"], CultureInfo.InvariantCulture);

            Assert.Equal(4u, priority);
        }
        finally
        {
            await wmi.InvokeMethodAsync(
                $"Win32_Process.Handle=\"{processId}\"", "Terminate", new Dictionary<string, object?> { ["Reason"] = 0 });
        }
    }
}
