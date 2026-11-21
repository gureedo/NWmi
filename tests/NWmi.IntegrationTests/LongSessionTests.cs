using System.Diagnostics;
using System.Globalization;
using NWmi.Auth;
using NWmi.Client;

namespace NWmi.IntegrationTests;

/// <summary>
/// A session held open longer than DCOM's object lifetime (3 missed 2-minute pings, MS-DCOM 3.1.2.2), and single objects
/// that need several RPC fragments. The idle test is opt-in (<c>NWMI_TEST_IDLE_SECONDS</c>) because it waits that long.
/// </summary>
public class LongSessionTests
{
    private const uint HklmKey = 0x80000002;
    private const string TestKey = @"SOFTWARE\NWmiBigObjectTest";

    private static Task<WmiConnection> ConnectAsync(string ns = @"root\cimv2") =>
        WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = ns });

    [IntegrationFact]
    public async Task A_session_idle_for_longer_than_the_object_lifetime_still_works()
    {
        var seconds = int.Parse(Environment.GetEnvironmentVariable("NWMI_TEST_IDLE_SECONDS") ?? "0", CultureInfo.InvariantCulture);
        if (seconds <= 0)
            Assert.Skip("Set NWMI_TEST_IDLE_SECONDS (e.g. 480) to hold a session idle that long.");

        await using var wmi = await ConnectAsync();
        await using var root = await ConnectAsync("root");
        await using var child = await root.OpenNamespaceAsync("cimv2");
        Assert.Single(await Collect(wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem")));

        // Start an enumeration, leave it half-read, and let everything sit.
        await using var open = wmi.QueryAsync("SELECT Name FROM Win32_Process").GetAsyncEnumerator();
        Assert.True(await open.MoveNextAsync());

        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);

        Assert.Single(await Collect(wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem")));
        Assert.Single(await Collect(child.QueryAsync("SELECT Caption FROM Win32_OperatingSystem")));
        Assert.True(await open.MoveNextAsync(), $"the half-read enumeration died after {clock.Elapsed}");
    }

    [IntegrationFact]
    public async Task The_connection_pings_its_objects_so_the_server_keeps_them_alive()
    {
        await using var wmi = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = @"root\cimv2", KeepAliveInterval = TimeSpan.FromSeconds(2) });

        var pinger = wmi.Services.Pinger;
        Assert.NotNull(pinger);
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

        Assert.True(pinger.PingCount > 0, "no ping was sent");
        Assert.Null(pinger.LastFailure);
        Assert.Single(await Collect(wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem")));
    }

    [IntegrationFact]
    public async Task A_single_object_of_hundreds_of_kilobytes_goes_in_and_comes_back_through_fragmentation()
    {
        await using var wmi = await ConnectAsync(@"root\default");
        var key = new Dictionary<string, object?> { ["hDefKey"] = HklmKey, ["sSubKeyName"] = TestKey };
        try
        {
            await Reg(wmi, "CreateKey", key);

            // 600,000 bytes: far more than one RPC fragment (the negotiated size is a few KB) each way.
            var data = new byte[600_000];
            new Random(42).NextBytes(data);
            var set = new Dictionary<string, object?>(key) { ["sValueName"] = "Blob", ["uValue"] = data };
            await Reg(wmi, "SetBinaryValue", set);

            var read = await Reg(wmi, "GetBinaryValue", new Dictionary<string, object?>(key) { ["sValueName"] = "Blob" });
            var back = ((object?[])read["uValue"]!).Cast<byte>().ToArray();
            Assert.Equal(data.Length, back.Length);
            Assert.True(data.AsSpan().SequenceEqual(back));

            // And many strings, one of them 100 KB long.
            string[] lines = [.. Enumerable.Range(0, 2000).Select(i => $"line {i} ünï ✓"), new('x', 100_000)];
            await Reg(wmi, "SetMultiStringValue", new Dictionary<string, object?>(key) { ["sValueName"] = "Lines", ["sValue"] = lines });
            var multi = await Reg(wmi, "GetMultiStringValue", new Dictionary<string, object?>(key) { ["sValueName"] = "Lines" });
            Assert.Equal(lines, ((object?[])multi["sValue"]!).Cast<string>());
        }
        finally
        {
            await wmi.InvokeMethodAsync("StdRegProv", "DeleteKey", key);
        }
    }

    private static async Task<WmiObject> Reg(WmiConnection wmi, string method, Dictionary<string, object?> parameters)
    {
        var result = await wmi.InvokeMethodAsync("StdRegProv", method, parameters);
        Assert.NotNull(result);
        Assert.Equal(0, Convert.ToInt32(result["ReturnValue"], CultureInfo.InvariantCulture));
        return result;
    }

    private static async Task<List<WmiObject>> Collect(IAsyncEnumerable<WmiObject> source)
    {
        var list = new List<WmiObject>();
        await foreach (var o in source)
            list.Add(o);

        return list;
    }
}
