using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using NWmi.Auth;
using NWmi.Client;
using NWmi.Rpc;
using NWmi.Wmi;

namespace NWmi.IntegrationTests;

/// <summary>
/// What the fake server cannot prove about the fixes in <c>bugs.md</c>. <c>NWMI_TEST_REPORT</c> names a file that collects the
/// timings and server answers these tests observe. <c>NWMI_TEST_CANCEL_ROUNDS</c> sets how often the cancellation tests repeat.
/// </summary>
public class ReviewFixTests
{
    private const uint HklmKey = 0x80000002;
    private const string NeverFires =
        "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = 'nwmi-never.exe'";

    private static int CancelRounds =>
        int.Parse(Environment.GetEnvironmentVariable("NWMI_TEST_CANCEL_ROUNDS") ?? "10", CultureInfo.InvariantCulture);

    /// <summary>Over Kerberos when NWMI_TEST_REALM, NWMI_TEST_KDC and NWMI_TEST_SPN are set, otherwise NTLM.</summary>
    private static Task<WmiConnection> ConnectAsync(WmiConnectionOptions options)
    {
        var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
        var realm = Environment.GetEnvironmentVariable("NWMI_TEST_REALM");
        var kdc = Environment.GetEnvironmentVariable("NWMI_TEST_KDC");
        var spn = Environment.GetEnvironmentVariable("NWMI_TEST_SPN");
        return string.IsNullOrEmpty(realm) || string.IsNullOrEmpty(kdc) || string.IsNullOrEmpty(spn)
            ? WmiConnection.ConnectAsync(IntegrationSettings.Host!, credential, options)
            : WmiConnection.ConnectAsync(IntegrationSettings.Host!, credential,
                new SpnegoOptions { Realm = realm, KdcHost = kdc, ServicePrincipalName = spn }, options);
    }

    private static void Report(string line)
    {
        if (Environment.GetEnvironmentVariable("NWMI_TEST_REPORT") is { Length: > 0 } path)
            File.AppendAllText(path, $"[{IntegrationSettings.Host}] {line}{Environment.NewLine}");
    }

    private static async Task<int> CountAsync(IAsyncEnumerable<WmiInstance> source)
    {
        var count = 0;
        await foreach (var _ in source)
            count++;

        return count;
    }

    private static Task<int> OneRowAsync(WmiConnection wmi) => CountAsync(wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem"));

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    [IntegrationFact]
    public async Task Cancelling_a_blocked_Next_leaves_the_connection_working()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions { Timeout = TimeSpan.FromSeconds(3) });
        var clock = new Stopwatch();
        for (var round = 0; round < CancelRounds; round++)
        {
            // The varying delay lands the cancellation on different calls: the query, the smart enum set-up, Next.
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20 + (round * 97 % 900)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CountAsync(wmi.WatchAsync(NeverFires, cancellationToken: cancel.Token)));

            clock.Restart();
            Assert.Equal(1, await OneRowAsync(wmi));
            Report($"#2 watch round {round}: next query took {clock.ElapsedMilliseconds} ms");
        }
    }

    [IntegrationFact]
    public async Task A_cancelled_watch_ends_at_once_though_the_server_is_still_blocked()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CountAsync(wmi.WatchAsync(NeverFires, cancellationToken: cancel.Token)));
        var ended = clock.Elapsed;
        Report($"#2 watch cancelled at 2 s ended after {ended.TotalMilliseconds:F0} ms");
        Assert.True(ended < TimeSpan.FromSeconds(4), $"took {ended}");

        clock.Restart();
        Assert.Equal(1, await OneRowAsync(wmi));
        Report($"#2 the next query waited {clock.ElapsedMilliseconds} ms for the abandoned Next");
    }

    [IntegrationFact]
    public async Task Cancelling_a_query_during_a_fetch_leaves_the_connection_working()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions { BatchSize = 1 });
        var finished = 0;
        for (var round = 0; round < CancelRounds; round++)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1 + (round * 7 % 120)));
            try
            {
                await CountAsync(wmi.QueryAsync("SELECT * FROM Win32_Process", cancellationToken: cancel.Token));
                finished++;
            }
            catch (OperationCanceledException)
            {
            }

            Assert.Equal(1, await OneRowAsync(wmi));
        }

        Report($"#2 query: {CancelRounds} rounds, {finished} finished before the cancellation");
    }

    [IntegrationFact]
    public async Task A_Next_that_outlasts_the_RPC_timeout_is_abandoned_and_the_next_query_works()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions
        {
            Timeout = TimeSpan.FromSeconds(10),
            RpcTimeout = TimeSpan.FromSeconds(6),
        });

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => CountAsync(wmi.WatchAsync(NeverFires)));
        Report($"#1/#2 RPC timeout surfaced after {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        Assert.Equal(1, await OneRowAsync(wmi));
        Report($"#1/#2 next query took {clock.ElapsedMilliseconds} ms");
    }

    [IntegrationFact]
    public async Task An_idle_watch_may_block_for_longer_than_60_seconds()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions { Timeout = TimeSpan.FromSeconds(75) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(70));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CountAsync(wmi.WatchAsync(NeverFires, cancellationToken: cancel.Token)));

        var clock = Stopwatch.StartNew();
        Assert.Equal(1, await OneRowAsync(wmi));
        Report($"#1 after a 70 s watch the next query took {clock.ElapsedMilliseconds} ms");
    }

    [IntegrationFact]
    public async Task Pings_resume_on_a_new_connection_with_the_same_ping_set()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions { KeepAliveInterval = TimeSpan.FromHours(1) });
        var pinger = wmi.Services.Pinger!;
        var token = TestContext.Current.CancellationToken;

        await pinger.PingNowAsync(token);
        Assert.Null(pinger.LastFailure);
        var setId = (ulong)Field(pinger, "_setId");

        var lost = (RpcClient)Field(Field(pinger, "_exporter"), "_client");
        await lost.DisposeAsync();
        await pinger.PingNowAsync(token);
        Assert.NotNull(pinger.LastFailure);
        Report($"#12 after losing the connection: {pinger.LastFailure!.GetType().Name}: {pinger.LastFailure.Message}");

        await pinger.PingNowAsync(token);
        Assert.Null(pinger.LastFailure);
        Assert.Equal(setId, (ulong)Field(pinger, "_setId"));

        // A half-read query adds OIDs, so this one is a ComplexPing of the old set on the new connection.
        await using var open = wmi.QueryAsync("SELECT Name FROM Win32_Process").GetAsyncEnumerator(token);
        Assert.True(await open.MoveNextAsync());
        await pinger.PingNowAsync(token);
        Assert.Null(pinger.LastFailure);
        Report($"#12 pings resumed on set {setId:x}, {pinger.PingCount} succeeded");
    }

    [IntegrationFact]
    public async Task An_interface_the_object_lacks_is_reported_as_missing()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions());
        var connection = (WmiOrpcConnection)Field(wmi.Services, "_connection");

        var missing = await connection.QueryInterfaceAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(missing);
        Assert.Equal(1, await OneRowAsync(wmi));
    }

    /// <summary>
    /// Records what the server sends for an inherited default. <c>NWMI_TEST_INHERIT=1</c> needs, in <c>root\default</c>, a class
    /// <c>NWmiBase</c> with key <c>Id</c>, a string <c>Color</c> defaulting to <c>blue</c>, a uint32 <c>Count</c> defaulting to 7,
    /// a string array <c>Tags</c> defaulting to <c>{"a", "b"}</c> and a string <c>NoDefault</c> with no default, a subclass <c>NWmiDerived</c>, and its instance
    /// <c>one</c> with <c>Color</c> left alone.
    /// </summary>
    [IntegrationFact]
    public async Task What_the_server_sends_for_an_inherited_default()
    {
        if (Environment.GetEnvironmentVariable("NWMI_TEST_INHERIT") != "1")
            Assert.Skip("Set NWMI_TEST_INHERIT=1 once the classes exist.");

        await using var wmi = await ConnectAsync(new WmiConnectionOptions { Namespace = @"root\default" });
        var one = await wmi.GetInstanceAsync("NWmiDerived.Id=\"one\"");
        var readers = new List<Func<string, object?>> { name => one[name] };
        foreach (var className in new[] { "NWmiBase", "NWmiDerived" })
        {
            var @class = await wmi.GetClassAsync(className);
            readers.Add(name => @class.Properties[name]!.Value);
        }

        foreach (var found in readers)
        {
            Assert.Equal("blue", found("Color"));
            Assert.Equal(7u, found("Count"));
            Assert.Equal(["a", "b"], ((object?[])found("Tags")!).Cast<string>());
            Assert.Null(found("NoDefault"));
        }

        // A value set on the instance still wins, and one the instance leaves alone survives a trip through PutInstance.
        var instance = (await wmi.GetInstanceAsync("NWmiDerived.Id=\"one\""))!;
        instance["NoDefault"] = "set";
        await wmi.PutInstanceAsync(instance, WbemChangeFlags.UpdateOnly);
        var back = (await wmi.GetInstanceAsync("NWmiDerived.Id=\"one\""))!;
        back["NoDefault"] = null;
        await wmi.PutInstanceAsync(back, WbemChangeFlags.UpdateOnly);
        Assert.Equal("set", instance["NoDefault"]);
        Assert.Equal("blue", back["Color"]);
    }

    /// <summary>
    /// A reference dropped without a release costs WMI's host about 5 handles and 22 KB until DCOM's timeout, minutes after the session
    /// ends. <c>NWMI_TEST_LEAK_ROUNDS</c> (e.g. 500) cancels that many queries at moments that hit the calls returning references.
    /// </summary>
    [IntegrationFact]
    public async Task Cancelled_queries_leave_nothing_behind_on_the_server()
    {
        var rounds = int.Parse(Environment.GetEnvironmentVariable("NWMI_TEST_LEAK_ROUNDS") ?? "0", CultureInfo.InvariantCulture);
        if (rounds <= 0)
            Assert.Skip("Set NWMI_TEST_LEAK_ROUNDS (e.g. 500) to measure this.");

        await using var observer = await ConnectAsync(new WmiConnectionOptions());
        var host = Convert.ToUInt32((await observer.GetInstanceAsync("Win32_Service.Name=\"Winmgmt\""))!["ProcessId"], CultureInfo.InvariantCulture);

        async Task<int> HandlesAsync() =>
            Convert.ToInt32((await observer.GetInstanceAsync($"Win32_Process.Handle=\"{host}\""))!["HandleCount"], CultureInfo.InvariantCulture);

        var before = await HandlesAsync();
        await using var wmi = await ConnectAsync(new WmiConnectionOptions());
        var cancelled = 0;
        for (var round = 0; round < rounds; round++)
        {
            using var cancel = new CancellationTokenSource();
            var query = CountAsync(wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem", cancellationToken: cancel.Token));

            // Spinning, because timers here are far coarser than the few milliseconds a whole query takes.
            var clock = Stopwatch.StartNew();
            var moment = TimeSpan.FromMicroseconds(round % 40 * 150);
            while (clock.Elapsed < moment)
                Thread.SpinWait(50);

            await cancel.CancelAsync();
            try
            {
                await query;
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }
        }

        Assert.Equal(1, await OneRowAsync(wmi));
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var after = await HandlesAsync();
        Report($"leak: {rounds} rounds, {cancelled} cancelled, WMI host handles {before} -> {after}");
        Assert.True(after - before < rounds / 5, $"handles grew from {before} to {after}");
    }

    /// <summary>Given a slot for it, Windows returns a call result even from a synchronous GetObject, so none is offered.</summary>
    [IntegrationFact]
    public async Task A_synchronous_GetObject_returns_the_object_and_no_call_result()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions());
        var token = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "Win32_Process", "Win32_OperatingSystem=@", "Win32_Service.Name=\"Winmgmt\"" })
        {
            var result = await wmi.Services.GetObjectAsync(path, cancellationToken: token);

            Assert.NotNull(result.Object);
            Assert.Null(result.CallResult);
        }

        var missing = await Assert.ThrowsAsync<WmiException>(() => wmi.Services.GetObjectAsync("NWmi_NoSuchClass", cancellationToken: token));
        Assert.Equal(unchecked((int)WbemStatus.NotFound), missing.HResult);
    }

    /// <summary>
    /// <c>NWMI_TEST_DATES=1</c> also needs a class <c>NWmiHolder</c> with key <c>Id</c>, a string <c>Label</c>, and <c>Inner</c>, an embedded
    /// <c>object:NWmiDates</c>.
    /// </summary>
    [IntegrationFact]
    public async Task Dates_inside_an_embedded_object_reach_the_server_over_WinRM_and_survive_being_put_back()
    {
        if (Environment.GetEnvironmentVariable("NWMI_TEST_DATES") != "1")
            Assert.Skip("Set NWMI_TEST_DATES=1 once the classes exist.");

        await RequireWinRmAsync();
        const string Moment = "20260102030405.678000+120";
        const string Span = "00000001020304.500000:000";
        const string Path = "NWmiHolder.Id=\"holder\"";
        await using var wmi = await ConnectWinRmAsync(@"root\default");
        await using var check = await ConnectAsync(new WmiConnectionOptions { Namespace = @"root\default" });

        var inner = new WmiInstance(await wmi.GetClassAsync("NWmiDates"));
        inner["Id"] = "inner";
        inner["When"] = Moment;
        inner["Span"] = Span;
        var holder = new WmiInstance(await wmi.GetClassAsync("NWmiHolder"));
        holder["Id"] = "holder";
        holder["Inner"] = inner;
        await wmi.PutInstanceAsync(holder, WbemChangeFlags.CreateOnly);
        try
        {
            var stored = Assert.IsType<WmiInstance>((await check.GetInstanceAsync(Path))!["Inner"]);
            Assert.Equal(Moment, stored["When"]);
            Assert.Equal(Span, stored["Span"]);

            // Read back over WinRM, the embedded dates are DMTF strings, which the Put has to convert.
            var read = (await wmi.GetInstanceAsync(Path))!;
            read["Label"] = "changed";
            await wmi.PutInstanceAsync(read, WbemChangeFlags.UpdateOnly);

            var after = (await check.GetInstanceAsync(Path))!;
            Assert.Equal("changed", after["Label"]);
            Assert.Equal(Moment, Assert.IsType<WmiInstance>(after["Inner"])["When"]);
        }
        finally
        {
            await check.DeleteInstanceAsync(Path);
        }
    }

    private static Task RequireWinRmAsync() => IntegrationSettings.RequireWinRmAsync();

    /// <summary>
    /// <c>NWMI_TEST_SLOW_DIR</c> names a directory on the host that takes more than a few seconds to copy. The method has to outlast
    /// an RPC timeout of 3 s, which a single synchronous call could not.
    /// </summary>
    [IntegrationFact]
    public async Task A_method_may_run_for_longer_than_the_RPC_timeout()
    {
        var source = Environment.GetEnvironmentVariable("NWMI_TEST_SLOW_DIR");
        if (string.IsNullOrEmpty(source))
            Assert.Skip("Set NWMI_TEST_SLOW_DIR to a directory that is slow to copy.");

        await using var wmi = await ConnectAsync(new WmiConnectionOptions
        {
            Timeout = TimeSpan.FromSeconds(1),
            RpcTimeout = TimeSpan.FromSeconds(3),
        });
        var target = source + "Copy" + Guid.NewGuid().ToString("N")[..8];
        static string DirectoryPath(string path) => $"Win32_Directory.Name=\"{path.Replace(@"\", @"\\", StringComparison.Ordinal)}\"";

        var clock = Stopwatch.StartNew();
        try
        {
            var copied = await wmi.InvokeMethodAsync(DirectoryPath(source), "Copy", new Dictionary<string, object?> { ["FileName"] = target });
            Report($"#1 Win32_Directory.Copy took {clock.ElapsedMilliseconds} ms and returned {copied?["ReturnValue"]}");

            Assert.Equal(0, Convert.ToInt32(copied!["ReturnValue"], CultureInfo.InvariantCulture));
            Assert.True(clock.Elapsed > TimeSpan.FromSeconds(3), $"the copy took only {clock.Elapsed}, so this proves nothing");
            Assert.Equal(1, await OneRowAsync(wmi));
        }
        finally
        {
            await wmi.InvokeMethodAsync(DirectoryPath(target), "Delete");
        }
    }

    [IntegrationFact]
    public async Task A_method_error_comes_back_through_the_call_result()
    {
        await using var wmi = await ConnectAsync(new WmiConnectionOptions());

        var missing = await Assert.ThrowsAsync<WmiException>(() => wmi.InvokeMethodAsync("NWmi_NoSuchClass", "Nothing"));
        var badMethod = await Assert.ThrowsAsync<WmiException>(() => wmi.InvokeMethodAsync("Win32_Process", "NoSuchMethod"));

        Report($"methods: missing class {missing.StatusName}, missing method {badMethod.StatusName}");
    }

    /// <summary>
    /// <c>NWMI_TEST_DATES=1</c> needs, in <c>root\default</c>, a class <c>NWmiDates</c> with key <c>Id</c>, datetimes <c>When</c> and
    /// <c>Span</c> (with SubType interval), and a string <c>Label</c>.
    /// </summary>
    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dates_and_intervals_reach_the_server_and_survive_being_put_back(bool winRm)
    {
        if (Environment.GetEnvironmentVariable("NWMI_TEST_DATES") != "1")
            Assert.Skip("Set NWMI_TEST_DATES=1 once the class exists.");

        if (winRm)
            await RequireWinRmAsync();

        await using var wmi = winRm
            ? await ConnectWinRmAsync(@"root\default")
            : await ConnectAsync(new WmiConnectionOptions { Namespace = @"root\default" });
        await using var check = await ConnectAsync(new WmiConnectionOptions { Namespace = @"root\default" });
        var id = "dates" + winRm;
        var path = $"NWmiDates.Id=\"{id}\"";
        var moment = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(2));
        var span = new TimeSpan(1, 2, 3, 4, 500);

        var created = new WmiInstance(await wmi.GetClassAsync("NWmiDates"));
        created["Id"] = id;
        created["When"] = moment;
        created["Span"] = span;
        await wmi.PutInstanceAsync(created, WbemChangeFlags.CreateOnly);
        try
        {
            var stored = (await check.GetInstanceAsync(path))!;
            Report($"#7 winRm={winRm}: stored When '{stored["When"]}', Span '{stored["Span"]}'");
            Assert.Equal(DmtfDateTime.Format(moment.ToUniversalTime()), DmtfDateTime.Format(((DateTimeOffset)DmtfDateTime.Parse((string)stored["When"]!)).ToUniversalTime()));
            Assert.Equal(DmtfDateTime.Format(span), stored["Span"]);

            // Read back through the same transport, the values are DMTF strings, which a Put has to accept.
            var read = (await wmi.GetInstanceAsync(path))!;
            read["Label"] = "changed";
            await wmi.PutInstanceAsync(read, WbemChangeFlags.UpdateOnly);

            var after = (await check.GetInstanceAsync(path))!;
            Assert.Equal("changed", after["Label"]);
            Assert.Equal(stored["When"], after["When"]);
            Assert.Equal(stored["Span"], after["Span"]);
        }
        finally
        {
            await check.DeleteInstanceAsync(path);
        }
    }

    private static Task<WmiConnection> ConnectWinRmAsync(string ns) =>
        WmiConnection.ConnectWinRmAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = ns });

    [IntegrationTheory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(20_000)]
    [InlineData(60_000)]
    public async Task A_sealed_WinRM_request_of_any_size_gets_through(int characters)
    {
        await RequireWinRmAsync();
        await using var wmi = await ConnectWinRmAsync(@"root\default");
        var key = new Dictionary<string, object?> { ["hDefKey"] = HklmKey, ["sSubKeyName"] = @"SOFTWARE\NWmiWinRmSizeTest" };
        var value = new string('x', characters);
        try
        {
            await wmi.InvokeMethodAsync("StdRegProv", "CreateKey", key);
            var set = await wmi.InvokeMethodAsync("StdRegProv", "SetStringValue", new Dictionary<string, object?>(key) { ["sValueName"] = "Big", ["sValue"] = value });
            Assert.Equal(0, Convert.ToInt32(set!["ReturnValue"], CultureInfo.InvariantCulture));

            var read = await wmi.InvokeMethodAsync("StdRegProv", "GetStringValue", new Dictionary<string, object?>(key) { ["sValueName"] = "Big" });
            Assert.Equal(value, (string?)read!["sValue"]);
        }
        finally
        {
            await wmi.InvokeMethodAsync("StdRegProv", "DeleteKey", key);
        }
    }

    [IntegrationFact]
    public async Task A_method_call_after_WinRM_sat_idle()
    {
        var seconds = int.Parse(Environment.GetEnvironmentVariable("NWMI_TEST_WINRM_IDLE_SECONDS") ?? "0", CultureInfo.InvariantCulture);
        if (seconds <= 0)
            Assert.Skip("Set NWMI_TEST_WINRM_IDLE_SECONDS (e.g. 150) to leave the session idle that long.");

        await RequireWinRmAsync();
        await using var wmi = await ConnectWinRmAsync(@"root\cimv2");
        Assert.Equal(1, await OneRowAsync(wmi));

        await Task.Delay(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);

        var parameters = new Dictionary<string, object?> { ["CommandLine"] = "cmd.exe /c exit" };
        var result = await wmi.InvokeMethodAsync("Win32_Process", "Create", parameters);
        Report($"#4 method after {seconds} s idle returned {result?["ReturnValue"]}");
        Assert.Equal(0, Convert.ToInt32(result!["ReturnValue"], CultureInfo.InvariantCulture));
    }

    /// <summary>The service restart drops every WinRM connection on the host, so it runs on its own, after the other tests.</summary>
    [Collection(nameof(ServiceRestart))]
    public class ServiceRestart
    {
        /// <summary>
        /// <c>NWMI_TEST_RESTART_WINRM=1</c> restarts the WinRM service under a session, which drops its connection for certain, then calls a
        /// method. The method appends one line to a file, so its size shows whether it ran once.
        /// </summary>
        [IntegrationFact]
        public async Task A_method_call_after_the_listener_dropped_the_connection_runs_exactly_once()
        {
            if (Environment.GetEnvironmentVariable("NWMI_TEST_RESTART_WINRM") != "1")
                Assert.Skip("Set NWMI_TEST_RESTART_WINRM=1 to restart the WinRM service on the host.");

            await RequireWinRmAsync();
            const string File = @"C:\Windows\Temp\nwmi-once.txt";
            var filePath = $"CIM_DataFile.Name=\"{File.Replace(@"\", @"\\", StringComparison.Ordinal)}\"";
            var token = TestContext.Current.CancellationToken;
            await using var dcom = await ConnectAsync(new WmiConnectionOptions());
            await using var wmi = await ConnectWinRmAsync(@"root\cimv2");
            Assert.Equal(1, await OneRowAsync(wmi));

            async Task SetServiceAsync(string method, string state)
            {
                await dcom.InvokeMethodAsync("Win32_Service.Name=\"WinRM\"", method, cancellationToken: token);
                for (var i = 0; i < 60 && (string?)(await dcom.GetInstanceAsync("Win32_Service.Name=\"WinRM\""))!["State"] != state; i++)
                    await Task.Delay(500, token);
            }

            await SetServiceAsync("StopService", "Stopped");
            await SetServiceAsync("StartService", "Running");
            await Task.Delay(3000, token);

            try
            {
                var result = await wmi.InvokeMethodAsync("Win32_Process", "Create",
                    new Dictionary<string, object?> { ["CommandLine"] = $"cmd.exe /c echo x>>{File}" }, cancellationToken: token);
                Report($"#4 method after the service restart returned {result?["ReturnValue"]}");
                Assert.Equal(0, Convert.ToInt32(result!["ReturnValue"], CultureInfo.InvariantCulture));

                await Task.Delay(3000, token);
                var written = (await dcom.GetInstanceAsync(filePath))!;
                Report($"#4 the file holds {written["FileSize"]} bytes; one run writes 3");
                Assert.Equal(3ul, Convert.ToUInt64(written["FileSize"], CultureInfo.InvariantCulture));
            }
            finally
            {
                await dcom.InvokeMethodAsync(filePath, "Delete", cancellationToken: CancellationToken.None);
            }
        }
    }
}

[CollectionDefinition(nameof(ReviewFixTests.ServiceRestart), DisableParallelization = true)]
public sealed class ServiceRestartRunsAlone;
