using NWmi.Auth;
using NWmi.Client;

namespace NWmi.IntegrationTests;

/// <summary>The .NET types of datetimes, references and arrays as a real server gives them, over DCOM and WinRM.</summary>
public class PropertyValueTests
{
    private static async Task<WmiConnection> ConnectAsync(bool winRm)
    {
        var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
        if (!winRm)
            return await WmiConnection.ConnectAsync(IntegrationSettings.Host!, credential);

        await IntegrationSettings.RequireWinRmAsync();
        return await WmiConnection.ConnectWinRmAsync(IntegrationSettings.Host!, credential);
    }

    private static async Task<List<WmiInstance>> QueryAsync(WmiConnection wmi, string query)
    {
        var list = new List<WmiInstance>();
        await foreach (var item in wmi.QueryAsync(query, cancellationToken: TestContext.Current.CancellationToken))
            list.Add(item);

        return list;
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_timestamp_is_a_UTC_DateTime(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);

        var os = Assert.Single(await QueryAsync(wmi, "SELECT * FROM Win32_OperatingSystem"));

        var boot = Assert.IsType<DateTime>(os["LastBootUpTime"]);
        Assert.Equal(DateTimeKind.Utc, boot.Kind);
        Assert.InRange(boot, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow.AddDays(1));
        Assert.Equal(WmiType.DateTime, os.Properties["LastBootUpTime"]!.Type);
    }

    [IntegrationFact]
    public async Task Both_transports_read_the_same_instant()
    {
        await using var dcom = await ConnectAsync(winRm: false);
        await using var winRm = await ConnectAsync(winRm: true);

        var overDcom = Assert.Single(await QueryAsync(dcom, "SELECT LastBootUpTime FROM Win32_OperatingSystem"));
        var overWinRm = Assert.Single(await QueryAsync(winRm, "SELECT LastBootUpTime FROM Win32_OperatingSystem"));

        Assert.Equal(overDcom["LastBootUpTime"], overWinRm["LastBootUpTime"]);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reference_is_an_instance_holding_the_keys_of_the_one_it_refers_to(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var ct = TestContext.Current.CancellationToken;

        var logon = (await QueryAsync(wmi, "SELECT * FROM Win32_LoggedOnUser"))[0];

        var account = Assert.IsType<WmiInstance>(logon["Antecedent"]);
        Assert.Equal(WmiType.Reference, logon.Properties["Antecedent"]!.Type);
        Assert.Equal("Win32_Account", account.SystemProperties.ClassName);
        Assert.Equal(@"root\cimv2", account.SystemProperties.Namespace, ignoreCase: true);
        Assert.Equal(["Domain", "Name"], account.Properties.Select(p => p.Name).Order());
        Assert.All(account.Properties, p => Assert.True(p.Flags.HasFlag(WmiFlags.Key)));
        Assert.IsType<string>(account["Name"]);

        var session = Assert.IsType<WmiInstance>(logon["Dependent"]);
        Assert.Equal("Win32_LogonSession", session.SystemProperties.ClassName);

        // The association's path still names it: its keys are the referenced paths. (WinRM selectors cannot hold a
        // reference given as a path.)
        if (!winRm)
        {
            var again = await wmi.GetInstanceAsync(logon.SystemProperties.RelativePath!, cancellationToken: ct);
            Assert.Equal(session["LogonId"], ((WmiInstance)again["Dependent"]!)["LogonId"]);
        }

        // The referenced instance can be fetched by its path.
        var fetched = await wmi.GetInstanceAsync(session.SystemProperties.Path!, cancellationToken: ct);
        Assert.Equal(session["LogonId"], fetched["LogonId"]);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reference_key_has_the_type_the_referenced_class_declares(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);

        var setting = (await QueryAsync(wmi, "SELECT * FROM Win32_NetworkAdapterSetting"))[0];

        var configuration = Assert.IsType<WmiInstance>(setting["Setting"]);
        Assert.IsType<uint>(configuration["Index"]);
        Assert.Equal(WmiType.UInt32, configuration.Properties["Index"]!.Type);
        Assert.IsType<string>(((WmiInstance)setting["Element"]!)["DeviceID"]);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Arrays_are_arrays_of_their_element_type(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);

        var adapters = await QueryAsync(wmi,
            "SELECT IPAddress, IPSubnet FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
        var os = Assert.Single(await QueryAsync(wmi, "SELECT MUILanguages FROM Win32_OperatingSystem"));

        Assert.NotEmpty(adapters);
        Assert.All(adapters, a => Assert.NotEmpty(Assert.IsType<string[]>(a["IPAddress"])));
        Assert.NotEmpty(Assert.IsType<string[]>(os["MUILanguages"]));
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_numeric_array_is_typed(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);

        var bios = Assert.Single(await QueryAsync(wmi, "SELECT * FROM Win32_BIOS"));

        Assert.Equal(WmiType.UInt16Array, bios.Properties["BiosCharacteristics"]!.Type);
        Assert.NotEmpty(Assert.IsType<ushort[]>(bios["BiosCharacteristics"]));
    }
}
