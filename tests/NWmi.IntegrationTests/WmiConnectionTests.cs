using System.Globalization;
using NWmi.Auth;
using NWmi.Options;
using NWmi.Wmi;

namespace NWmi.IntegrationTests;

public class WmiConnectionTests
{
    private static Task<WmiConnection> ConnectAsync(string ns = @"root\cimv2") =>
        WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = ns });

    [IntegrationFact]
    public async Task Query_returns_operating_system()
    {
        await using var wmi = await ConnectAsync();

        var results = new List<WmiInstance>();
        await foreach (var row in wmi.QueryAsync("SELECT Caption, Version FROM Win32_OperatingSystem"))
            results.Add(row);

        var os = Assert.Single(results);
        Assert.Equal("Win32_OperatingSystem", os.SystemProperties.ClassName);
        Assert.NotNull(os["Caption"]);
        Assert.NotNull(os["Version"]);
    }

    [IntegrationFact]
    public async Task GetClass_returns_a_class()
    {
        await using var wmi = await ConnectAsync();

        var cls = await wmi.GetClassAsync("Win32_OperatingSystem");

        Assert.Equal("Win32_OperatingSystem", cls.Name);
        Assert.NotNull(cls.Methods["Reboot"]);
    }

    [IntegrationFact]
    public async Task Large_enumeration_pages_through_fragmentation()
    {
        await using var wmi = await ConnectAsync();

        var count = 0;
        await foreach (var _ in wmi.QueryAsync("SELECT * FROM Win32_NTLogEvent WHERE Logfile='System'"))
        {
            count++;
            if (count >= 300)
                break;
        }

        Assert.True(count > 0, "expected at least one System event log entry");
    }

    [IntegrationFact]
    public async Task InvokeMethod_creates_and_terminates_a_process()
    {
        await using var wmi = await ConnectAsync();

        var created = await wmi.InvokeMethodAsync(
            "Win32_Process", "Create", new Dictionary<string, object?> { ["CommandLine"] = "notepad.exe" });

        Assert.NotNull(created);
        Assert.Equal(0, Convert.ToInt32(created["ReturnValue"], CultureInfo.InvariantCulture));
        var processId = Convert.ToUInt32(created["ProcessId"], CultureInfo.InvariantCulture);
        Assert.True(processId > 0);

        var terminated = await wmi.InvokeMethodAsync(
            $"Win32_Process.Handle=\"{processId}\"", "Terminate", new Dictionary<string, object?> { ["Reason"] = 0 });

        Assert.NotNull(terminated);
        Assert.Equal(0, Convert.ToInt32(terminated["ReturnValue"], CultureInfo.InvariantCulture));
    }

    [IntegrationFact]
    public async Task PutInstance_then_DeleteInstance_round_trips_a_namespace()
    {
        await using var wmi = await ConnectAsync(@"root\default");

        var nsClass = new WmiInstance(await wmi.GetClassAsync("__Namespace"));
        Assert.NotNull(nsClass);
        nsClass["Name"] = "nwmitest";

        await wmi.PutInstanceAsync(nsClass, WbemChangeFlags.CreateOnly);

        var created = await wmi.GetInstanceAsync("__Namespace.Name=\"nwmitest\"");
        Assert.NotNull(created);
        Assert.Equal("nwmitest", created["Name"]);

        await wmi.DeleteInstanceAsync("__Namespace.Name=\"nwmitest\"");

        await Assert.ThrowsAsync<WmiException>(() => wmi.GetInstanceAsync("__Namespace.Name=\"nwmitest\""));
    }

    [IntegrationFact]
    public async Task EnumerateClasses_lists_subclasses_of_a_superclass()
    {
        await using var wmi = await ConnectAsync();

        var deep = await ClassNames(wmi, "CIM_LogicalDevice", shallow: false);
        var shallow = await ClassNames(wmi, "CIM_LogicalDevice", shallow: true);

        Assert.Contains("Win32_Processor", deep);
        Assert.Contains("CIM_Processor", shallow);
        Assert.DoesNotContain("Win32_Processor", shallow);
        Assert.True(deep.Count > shallow.Count);
    }

    private static async Task<List<string>> ClassNames(WmiConnection wmi, string superClass, bool shallow)
    {
        var names = new List<string>();
        await foreach (var cls in wmi.EnumerateClassesAsync(superClass, shallow))
            names.Add(cls.Name);

        return names;
    }

    [IntegrationFact]
    public async Task OpenNamespace_reaches_a_child_namespace_over_the_same_session()
    {
        await using var root = await ConnectAsync("root");
        await using var cimv2 = await root.OpenNamespaceAsync("cimv2");

        var os = Assert.Single(await Collect(cimv2.QueryAsync("SELECT Caption FROM Win32_OperatingSystem")));
        Assert.NotNull(os["Caption"]);
        Assert.Equal(@"root\cimv2",
            cimv2.GetType().GetField("_namespacePath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cimv2));

        await Assert.ThrowsAsync<WmiException>(() => root.OpenNamespaceAsync("no_such_namespace"));
    }

    private static async Task<List<WmiInstance>> Collect(IAsyncEnumerable<WmiInstance> source)
    {
        var list = new List<WmiInstance>();
        await foreach (var o in source)
            list.Add(o);

        return list;
    }

    [IntegrationFact]
    public async Task WatchAsync_delivers_an_instance_creation_event()
    {
        await using var watcher = await ConnectAsync();
        await using var actor = await ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var events = watcher.WatchAsync(
            "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'",
            cancellationToken: timeout.Token);
        await using var enumerator = events.GetAsyncEnumerator(timeout.Token);
        var first = enumerator.MoveNextAsync().AsTask();

        await Task.Delay(2000, timeout.Token);
        var created = await actor.InvokeMethodAsync(
            "Win32_Process", "Create",
            new Dictionary<string, object?> { ["CommandLine"] = "cmd.exe /c ping -n 5 127.0.0.1" },
            cancellationToken: timeout.Token);
        Assert.Equal(0, Convert.ToInt32(created!["ReturnValue"], CultureInfo.InvariantCulture));

        Assert.True(await first);
        var evt = enumerator.Current;
        Assert.Equal("__InstanceCreationEvent", evt.SystemProperties.ClassName);
        var target = Assert.IsType<WmiInstance>(evt["TargetInstance"]);
        Assert.Equal("Win32_Process", target.SystemProperties.ClassName);
    }

    [IntegrationTheory]
    [InlineData(RpcAuthLevel.PacketIntegrity)]
    [InlineData(RpcAuthLevel.PacketPrivacy)]
    public async Task Packet_integrity_and_privacy_can_query_and_call_a_method(RpcAuthLevel level)
    {
        await using var wmi = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = @"root\cimv2", AuthLevel = level });

        var os = Assert.Single(await Collect(wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem")));
        Assert.NotNull(os["Caption"]);

        var created = await wmi.InvokeMethodAsync(
            "Win32_Process", "Create", new Dictionary<string, object?> { ["CommandLine"] = "cmd.exe /c exit" });
        Assert.Equal(0, Convert.ToInt32(created!["ReturnValue"], CultureInfo.InvariantCulture));
    }

    // A hardened Windows host (DCOM hardening, KB5004442) refuses anything below packet integrity.
    [IntegrationFact]
    public async Task A_hardened_host_rejects_connect_level_protection()
    {
        // WmiConnectionOptions may not allow Connect, so it is only reachable at this lower layer.
        var security = new Rpc.RpcSecurity(
            new NtlmSecurityProvider(Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!)),
            RpcAuthLevel.Connect);

        var ex = await Assert.ThrowsAsync<Rpc.RpcFaultException>(() =>
            WbemLevel1LoginClient.ConnectAsync(IntegrationSettings.Host!, security));
        Assert.Equal(0x00000005u, ex.Status); // ERROR_ACCESS_DENIED
    }

    private static async Task<uint> RecordCount(WmiConnection wmi, string logfile)
    {
        var file = Assert.Single(await Collect(
            wmi.QueryAsync($"SELECT NumberOfRecords FROM Win32_NTEventlogFile WHERE LogfileName='{logfile}'")));
        return Convert.ToUInt32(file["NumberOfRecords"], CultureInfo.InvariantCulture);
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_whole_event_log_streams_completely(bool smartEnumeration)
    {
        await using var wmi = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = @"root\cimv2", UseSmartEnumeration = smartEnumeration });

        var before = await RecordCount(wmi, "Security");
        var numbers = new HashSet<uint>();
        var total = 0;
        await foreach (var evt in wmi.QueryAsync(
                           "SELECT RecordNumber, EventCode, Message FROM Win32_NTLogEvent WHERE Logfile='Security'"))
        {
            total++;
            numbers.Add(Convert.ToUInt32(evt["RecordNumber"], CultureInfo.InvariantCulture));
        }

        var after = await RecordCount(wmi, "Security");
        Assert.True(total > 0, "expected at least one Security log entry");
        Assert.Equal(total, numbers.Count); // no object delivered twice
        // The Security log is a ring buffer that keeps changing while it is read, so compare with a tolerance.
        Assert.InRange(total, Math.Min(before, after) * 0.99, Math.Max(before, after) * 1.01);
    }

    [IntegrationFact]
    public async Task Tens_of_thousands_of_objects_stream_without_loss_or_duplication()
    {
        await using var wmi = await ConnectAsync();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        await foreach (var file in wmi.QueryAsync(
                           "SELECT Name, FileSize FROM CIM_DataFile WHERE Drive='C:' AND Extension='dll'"))
        {
            total++;
            names.Add((string)file["Name"]!);
        }

        Assert.Equal(total, names.Count);
        Assert.True(total > 20000, $"expected tens of thousands of files, got {total}");
    }

    [IntegrationFact]
    public async Task Breaking_out_of_a_huge_enumeration_releases_it_and_the_connection_stays_usable()
    {
        await using var wmi = await ConnectAsync();

        var seen = 0;
        await foreach (var _ in wmi.QueryAsync("SELECT Name FROM CIM_DataFile WHERE Drive='C:' AND Extension='dll'"))
            if (++seen == 100)
                break;

        Assert.Equal(100, seen);
        var os = Assert.Single(await Collect(wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem")));
        Assert.NotNull(os["Caption"]);
    }

    [IntegrationFact]
    public async Task Cancelling_midway_through_a_huge_enumeration_throws_promptly()
    {
        await using var wmi = await ConnectAsync();
        using var cts = new CancellationTokenSource();

        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in wmi.QueryAsync("SELECT Name FROM CIM_DataFile WHERE Drive='C:' AND Extension='dll'",
                               cancellationToken: cts.Token))
                if (++seen == 500)
                    cts.Cancel();
        });
        Assert.InRange(seen, 500, 500 + 2000);
    }

    [IntegrationFact]
    public async Task Every_class_in_a_namespace_can_be_enumerated_with_its_full_definition()
    {
        await using var wmi = await ConnectAsync();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withProperties = 0;
        await foreach (var cls in wmi.EnumerateClassesAsync())
        {
            names.Add(cls.Name);
            if (cls.Properties.Count > 0)
                withProperties++;
        }

        Assert.True(names.Count > 1000, $"expected over a thousand classes, got {names.Count}");
        Assert.Contains("Win32_Process", names);
        Assert.Contains("__Namespace", names);
        Assert.True(withProperties > 900);
    }
}
