using NWmi.Auth;
using NWmi.Client;
using NWmi.Wmi;

namespace NWmi.IntegrationTests;

/// <summary>Needs a host with a WinRM listener on 5985 (the domain machines in the lab; the workgroup Windows 11 VM has none).</summary>
public class WinRmTests
{
    /// <summary>
    /// <c>NWMI_TEST_WINRM_HTTPS=1</c> uses port 5986 (the certificate is not checked); <c>NWMI_TEST_WINRM_SPNEGO=realm;kdc;spn</c>
    /// negotiates Kerberos instead of using NTLM only.
    /// </summary>
    private static async Task<WmiConnection> ConnectAsync()
    {
        var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
        var https = Environment.GetEnvironmentVariable("NWMI_TEST_WINRM_HTTPS") == "1";
        var options = new WmiConnectionOptions { WinRmUseHttps = https, WinRmIgnoreCertificateErrors = https };
        await IntegrationSettings.RequireWinRmAsync(https ? 5986 : 5985);
        if (Environment.GetEnvironmentVariable("NWMI_TEST_WINRM_SPNEGO")?.Split(';') is [var realm, var kdc, var spn])
            return await WmiConnection.ConnectWinRmAsync(IntegrationSettings.Host!, credential,
                new SpnegoOptions { Realm = realm, KdcHost = kdc, ServicePrincipalName = spn }, options);

        return await WmiConnection.ConnectWinRmAsync(IntegrationSettings.Host!, credential, options);
    }

    [IntegrationFact]
    public async Task A_query_returns_objects_with_their_keys()
    {
        await using var wmi = await ConnectAsync();
        var rows = new List<WmiObject>();
        await foreach (var os in wmi.QueryAsync("SELECT Caption, Version FROM Win32_OperatingSystem"))
            rows.Add(os);

        var row = Assert.Single(rows);
        Assert.Equal("Win32_OperatingSystem", row.ClassName);
        Assert.Contains("Windows", (string?)row["Caption"]);
    }

    [IntegrationFact]
    public async Task EnumerateInstances_matches_a_SELECT_and_shallow_leaves_out_subclasses()
    {
        await using var wmi = await ConnectAsync();

        var enumerated = new List<string?>();
        await foreach (var disk in wmi.EnumerateInstancesAsync("Win32_LogicalDisk"))
            enumerated.Add((string?)disk["DeviceID"]);

        var queried = new List<string?>();
        await foreach (var disk in wmi.QueryAsync("SELECT * FROM Win32_LogicalDisk"))
            queried.Add((string?)disk["DeviceID"]);

        Assert.NotEmpty(enumerated);
        Assert.Equal(queried.Order(), enumerated.Order());

        // CIM_LogicalDisk is abstract: every instance is of a subclass.
        await foreach (var _ in wmi.EnumerateInstancesAsync("CIM_LogicalDisk", shallow: true))
            Assert.Fail("a shallow enumeration of an abstract class returned an instance");
    }

    [IntegrationFact]
    public async Task A_large_enumeration_is_pulled_in_batches()
    {
        await using var wmi = await ConnectAsync();
        var count = 0;
        await foreach (var process in wmi.QueryAsync("SELECT Handle, Name FROM Win32_Process"))
        {
            Assert.NotNull(process["Name"]);
            Assert.Equal("Win32_Process", process.ClassName);
            Assert.Contains("Handle", process.KeyPropertyNames);
            count++;
        }

        Assert.True(count > 30);
    }

    [IntegrationFact]
    public async Task GetObject_by_path_and_by_class()
    {
        await using var wmi = await ConnectAsync();
        var os = await wmi.GetObjectAsync("Win32_OperatingSystem=@");
        Assert.NotNull(os);
        Assert.IsType<bool>(os["Primary"]);
        Assert.Matches(@"^\d{14}\.\d{6}[+-]\d{3}$", (string)os["LastBootUpTime"]!);

        var system = await wmi.GetObjectAsync("Win32_Process.Handle=\"4\"");
        Assert.Equal("System", system!["Name"]);

        var cls = await wmi.GetObjectAsync("Win32_Process");
        Assert.NotNull(cls);
        Assert.Null(cls["Name"]);
    }

    [IntegrationFact]
    public async Task A_missing_class_is_a_WmiException()
    {
        await using var wmi = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<WmiException>(() => wmi.GetObjectAsync("Win32_Nope=@"));
        Assert.Equal("InvalidClass", ex.StatusName);
    }

    [IntegrationFact]
    public async Task A_method_runs_and_can_be_terminated()
    {
        await using var wmi = await ConnectAsync();
        var created = await wmi.InvokeMethodAsync(
            "Win32_Process", "Create", new Dictionary<string, object?> { ["CommandLine"] = "ping -n 30 127.0.0.1" });
        Assert.NotNull(created);
        Assert.Equal(0u, created["ReturnValue"]);
        var pid = Assert.IsType<uint>(created["ProcessId"]);

        var terminated = await wmi.InvokeMethodAsync(
            $"Win32_Process.Handle=\"{pid}\"", "Terminate", new Dictionary<string, object?> { ["Reason"] = 0 });
        Assert.Equal(0u, terminated!["ReturnValue"]);
    }

    [IntegrationFact]
    public async Task A_child_namespace_and_the_class_list_work()
    {
        await IntegrationSettings.RequireWinRmAsync();
        await using var root = await WmiConnection.ConnectWinRmAsync(
            IntegrationSettings.Host!, Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = "root" });
        await using var cimv2 = await root.OpenNamespaceAsync("cimv2");
        var os = await cimv2.GetObjectAsync("Win32_OperatingSystem=@");
        Assert.NotNull(os);
        Assert.Equal(@"root\cimv2", os.Namespace);

        var classes = 0;
        await foreach (var cls in cimv2.EnumerateClassesAsync("CIM_LogicalDisk"))
        {
            classes++;
            Assert.NotNull(cls.ClassName);
        }

        Assert.True(classes >= 1);
    }

    [IntegrationFact]
    public async Task An_instance_can_be_created_updated_and_deleted()
    {
        await using var wmi = await ConnectAsync();
        var name = "NWMI_WINRM_" + Guid.NewGuid().ToString("N")[..8];
        var env = await wmi.GetObjectAsync("Win32_Environment");
        Assert.NotNull(env);
        env["Name"] = name;
        env["UserName"] = "<SYSTEM>";
        env["VariableValue"] = "one";
        await wmi.PutInstanceAsync(env, WbemChangeFlags.CreateOnly);
        var path = $"Win32_Environment.Name=\"{name}\",UserName=\"<SYSTEM>\"";
        try
        {
            var created = await wmi.GetObjectAsync(path);
            Assert.Equal("one", created!["VariableValue"]);

            created["VariableValue"] = "two";
            await wmi.PutInstanceAsync(created, WbemChangeFlags.UpdateOnly);
            Assert.Equal("two", (await wmi.GetObjectAsync(path))!["VariableValue"]);
        }
        finally
        {
            await wmi.DeleteInstanceAsync(path);
        }

        await Assert.ThrowsAsync<WmiException>(() => wmi.GetObjectAsync(path));
    }

    [IntegrationFact]
    public async Task An_embedded_object_parameter_reaches_the_provider()
    {
        await using var wmi = await ConnectAsync();
        var startup = await wmi.GetObjectAsync("Win32_ProcessStartup");
        Assert.NotNull(startup);
        startup["PriorityClass"] = 0x40u;
        startup["ShowWindow"] = (ushort)0;
        var created = await wmi.InvokeMethodAsync(
            "Win32_Process", "Create",
            new Dictionary<string, object?>
            {
                ["CommandLine"] = "ping -n 30 127.0.0.1",
                ["ProcessStartupInformation"] = startup,
            });
        Assert.Equal(0u, created!["ReturnValue"]);
        var pid = Assert.IsType<uint>(created["ProcessId"]);
        try
        {
            // Right after creation the provider can still report 0; wait for the real base priority.
            object? priority = null;
            for (var i = 0; i < 20; i++)
            {
                priority = (await wmi.GetObjectAsync($"Win32_Process.Handle=\"{pid}\""))!["Priority"];
                if (!Equals(priority, 0u))
                    break;

                await Task.Delay(250);
            }

            Assert.Equal(4u, priority);
        }
        finally
        {
            await wmi.InvokeMethodAsync($"Win32_Process.Handle=\"{pid}\"", "Terminate");
        }
    }

    [IntegrationFact]
    public async Task Events_are_pulled()
    {
        await using var wmi = await ConnectAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var events = wmi.WatchAsync(
            "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = 'ping.exe'",
            cancellationToken: cts.Token);
        await using var starter = await ConnectAsync();
        var run = Task.Run(async () =>
        {
            await Task.Delay(2000);
            await starter.InvokeMethodAsync("Win32_Process", "Create",
                new Dictionary<string, object?> { ["CommandLine"] = "ping -n 3 127.0.0.1" });
        });

        await foreach (var ev in events)
        {
            Assert.Equal("__InstanceCreationEvent", ev.ClassName);
            Assert.IsType<WmiObject>(ev["TargetInstance"]);
            Assert.Equal("ping.exe", (string?)((WmiObject)ev["TargetInstance"]!)["Name"], ignoreCase: true);
            break;
        }

        await run;
    }

    [IntegrationFact]
    public async Task Values_are_typed_from_the_class_definition()
    {
        await using var wmi = await ConnectAsync();
        var os = await wmi.QueryAsync("SELECT * FROM Win32_OperatingSystem").FirstAsync();
        Assert.IsType<uint>(os["NumberOfProcesses"]);
        Assert.IsType<ulong>(os["TotalVisibleMemorySize"]);
        Assert.IsType<ushort>(os["OSType"]);
        Assert.IsType<bool>(os["Primary"]);
        Assert.IsType<string>(os["Caption"]);

        // An array property stays an array when it holds one element.
        var processes = await wmi.QueryAsync("SELECT * FROM Win32_Process WHERE Name = 'System'").ToListAsync();
        Assert.Single(processes);
        var adapters = await wmi.QueryAsync("SELECT IPAddress, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE")
            .ToListAsync();
        Assert.NotEmpty(adapters);
        Assert.All(adapters.Where(a => a["DNSServerSearchOrder"] is not null), a => Assert.IsType<object?[]>(a["DNSServerSearchOrder"]));
    }
}
