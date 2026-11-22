using NWmi.Auth;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NWmi.Options;
using NWmi.WinRm;

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
        var rows = new List<WmiInstance>();
        await foreach (var os in wmi.QueryAsync("SELECT Caption, Version FROM Win32_OperatingSystem"))
            rows.Add(os);

        var row = Assert.Single(rows);
        Assert.Equal("Win32_OperatingSystem", row.SystemProperties.ClassName);
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
            Assert.Equal("Win32_Process", process.SystemProperties.ClassName);
            Assert.Contains("Handle", process.KeyNames);
            count++;
        }

        Assert.True(count > 30);
    }

    [IntegrationFact]
    public async Task GetInstance_by_path_and_GetClass()
    {
        await using var wmi = await ConnectAsync();
        var os = await wmi.GetInstanceAsync("Win32_OperatingSystem=@");
        Assert.NotNull(os);
        Assert.IsType<bool>(os["Primary"]);
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(os["LastBootUpTime"]).Kind);

        var system = await wmi.GetInstanceAsync("Win32_Process.Handle=\"4\"");
        Assert.Equal("System", system!["Name"]);

        var cls = await wmi.GetClassAsync("Win32_Process");
        Assert.Equal("CIM_Process", cls.SuperClassName);
        Assert.True(cls.Properties["Handle"]!.Flags.HasFlag(WmiFlags.Key | WmiFlags.ReadOnly));
        Assert.Equal(WmiType.UInt32, cls.Methods["Create"]!.ReturnType);
        Assert.Equal(WmiType.Instance, cls.Methods["Create"]!.Parameters["ProcessStartupInformation"]!.Type);
    }

    [IntegrationFact]
    public async Task A_missing_class_is_a_WmiException()
    {
        await using var wmi = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<WmiException>(() => wmi.GetInstanceAsync("Win32_Nope=@"));
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
        var os = await cimv2.GetInstanceAsync("Win32_OperatingSystem=@");
        Assert.NotNull(os);
        Assert.Equal(@"root\cimv2", os.SystemProperties.Namespace);

        var classes = 0;
        await foreach (var cls in cimv2.EnumerateClassesAsync("CIM_LogicalDisk"))
        {
            classes++;
            Assert.NotNull(cls.Name);
        }

        Assert.True(classes >= 1);
    }

    [IntegrationFact]
    public async Task An_instance_can_be_created_updated_and_deleted()
    {
        await using var wmi = await ConnectAsync();
        var name = "NWMI_WINRM_" + Guid.NewGuid().ToString("N")[..8];
        var env = new WmiInstance(await wmi.GetClassAsync("Win32_Environment"));
        Assert.NotNull(env);
        env["Name"] = name;
        env["UserName"] = "<SYSTEM>";
        env["VariableValue"] = "one";
        await wmi.PutInstanceAsync(env, WbemChangeFlags.CreateOnly);
        var path = $"Win32_Environment.Name=\"{name}\",UserName=\"<SYSTEM>\"";
        try
        {
            var created = await wmi.GetInstanceAsync(path);
            Assert.Equal("one", created!["VariableValue"]);

            created["VariableValue"] = "two";
            await wmi.PutInstanceAsync(created, WbemChangeFlags.UpdateOnly);
            Assert.Equal("two", (await wmi.GetInstanceAsync(path))!["VariableValue"]);
        }
        finally
        {
            await wmi.DeleteInstanceAsync(path);
        }

        await Assert.ThrowsAsync<WmiException>(() => wmi.GetInstanceAsync(path));
    }

    [IntegrationFact]
    public async Task An_embedded_object_parameter_reaches_the_provider()
    {
        await using var wmi = await ConnectAsync();
        var startup = new WmiInstance(await wmi.GetClassAsync("Win32_ProcessStartup"));
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
                priority = (await wmi.GetInstanceAsync($"Win32_Process.Handle=\"{pid}\""))!["Priority"];
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
            Assert.Equal("__InstanceCreationEvent", ev.SystemProperties.ClassName);
            Assert.IsType<WmiInstance>(ev["TargetInstance"]);
            Assert.Equal("ping.exe", (string?)((WmiInstance)ev["TargetInstance"]!)["Name"], ignoreCase: true);
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
        Assert.All(adapters.Where(a => a["DNSServerSearchOrder"] is not null), a => Assert.IsType<string[]>(a["DNSServerSearchOrder"]));
    }

    [IntegrationFact]
    public async Task Unsubscribe_with_the_subscription_identifier_ends_the_subscription()
    {
        await IntegrationSettings.RequireWinRmAsync();
        var ct = TestContext.Current.CancellationToken;
        var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
        using var transport = new WsmanTransport(new Uri($"http://{IntegrationSettings.Host}:5985/wsman"),
            (binding, _) => Task.FromResult<IRpcSecurityProvider>(
                new NtlmSecurityProvider(credential, new NtlmSecurityOptions { ChannelBindings = binding })),
            TimeSpan.FromSeconds(60), NullLogger.Instance);
        await transport.ConnectAsync(ct);
        var client = new WsmanClient(transport, @"root\cimv2", TimeSpan.FromSeconds(30));
        var uri = client.ResourceUri("*");

        async Task<(XElement Response, string Context)> SubscribeAsync()
        {
            var body = await client.CallAsync(uri, Wsman.ActionSubscribe, null,
                new XElement(Wsman.Eventing + "Subscribe",
                    new XElement(Wsman.Eventing + "Delivery",
                        new XAttribute("Mode", "http://schemas.dmtf.org/wbem/wsman/1/wsman/Pull")),
                    new XElement(Wsman.Man + "Filter", new XAttribute("Dialect", Wsman.WqlDialect),
                        "SELECT * FROM __InstanceCreationEvent WITHIN 5 WHERE TargetInstance ISA 'Win32_Process'")), ct);
            var response = body.Element(Wsman.Eventing + "SubscribeResponse")!;
            return (response, response.Descendants(Wsman.Enumeration + "EnumerationContext").First().Value.Trim());
        }

        Task<XElement> PullAsync(string context) => client.CallAsync(uri, Wsman.ActionPull, null,
            new XElement(Wsman.Enumeration + "Pull",
                new XElement(Wsman.Enumeration + "EnumerationContext", context),
                new XElement(Wsman.Enumeration + "MaxElements", 1)), ct, TimeSpan.FromSeconds(1));

        var (response, context) = await SubscribeAsync();
        var headers = WinRmBackend.SubscriptionHeaders(response);
        Assert.True(headers.Count > 0, $"No reference parameters in: {response}");

        // Without the identifier the listener cannot tell which subscription is meant.
        await Assert.ThrowsAnyAsync<Exception>(() => client.CallAsync(uri, Wsman.ActionUnsubscribe, null,
            new XElement(Wsman.Eventing + "Unsubscribe"), ct));

        await client.CallAsync(uri, Wsman.ActionUnsubscribe, null, new XElement(Wsman.Eventing + "Unsubscribe"), ct,
            extraHeaders: headers);

        // The subscription is gone: its context no longer pulls (a live one would time out quietly instead).
        var pulled = await Record.ExceptionAsync(() => PullAsync(context));
        Assert.NotNull(pulled);
        Assert.IsNotType<WsmanTimedOutException>(pulled);
    }
}
