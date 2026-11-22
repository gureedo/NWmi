using NWmi.Auth;
using NWmi.Client;

namespace NWmi.IntegrationTests;

/// <summary>ASSOCIATORS OF and REFERENCES OF, over DCOM and (where the host has a listener) WinRM.</summary>
public class AssociationTests
{
    private static async Task<WmiConnection> ConnectAsync(bool winRm)
    {
        var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
        if (!winRm)
            return await WmiConnection.ConnectAsync(IntegrationSettings.Host!, credential);

        await IntegrationSettings.RequireWinRmAsync();
        return await WmiConnection.ConnectWinRmAsync(IntegrationSettings.Host!, credential);
    }

    private static async Task<List<WmiInstance>> ToListAsync(IAsyncEnumerable<WmiInstance> objects)
    {
        var list = new List<WmiInstance>();
        await foreach (var o in objects.WithCancellation(TestContext.Current.CancellationToken))
            list.Add(o);

        return list;
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Associators_are_narrowed_by_association_class_role_and_result_class(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        const string winRmService = "Win32_Service.Name=\"WinRM\"";
        var ct = TestContext.Current.CancellationToken;

        var all = await ToListAsync(wmi.GetAssociatorsAsync(winRmService, cancellationToken: ct));
        Assert.Contains(all, o => o.SystemProperties.ClassName == "Win32_ComputerSystem");

        // WinRM is the dependent of its dependencies: RPC and the HTTP driver.
        var dependencies = await ToListAsync(wmi.GetAssociatorsAsync(winRmService,
            associationClass: "Win32_DependentService", role: "Dependent", cancellationToken: ct));
        Assert.Contains(dependencies, o => o.SystemProperties.ClassName == "Win32_Service" && (string?)o["Name"] == "RpcSs");
        Assert.Contains(dependencies, o => o.SystemProperties.ClassName == "Win32_SystemDriver");
        Assert.DoesNotContain(dependencies, o => o.SystemProperties.ClassName == "Win32_ComputerSystem");

        var services = await ToListAsync(wmi.GetAssociatorsAsync(winRmService,
            associationClass: "Win32_DependentService", resultClass: "Win32_Service", role: "Dependent",
            resultRole: "Antecedent", cancellationToken: ct));
        Assert.NotEmpty(services);
        Assert.All(services, o => Assert.Equal("Win32_Service", o.SystemProperties.ClassName));
        Assert.Contains(services, o => (string?)o["Name"] == "RpcSs");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_path_with_backslashes_reaches_the_server_intact(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var parents = await ToListAsync(wmi.GetAssociatorsAsync(@"Win32_Directory.Name=""C:\\Windows\\System32""",
            associationClass: "Win32_SubDirectory", role: "PartComponent",
            cancellationToken: TestContext.Current.CancellationToken));

        var parent = Assert.Single(parents);
        Assert.Equal(@"c:\windows", (string?)parent["Name"], ignoreCase: true);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task References_are_the_association_instances_keyed_by_their_two_ends(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var ct = TestContext.Current.CancellationToken;

        var all = await ToListAsync(wmi.GetReferencesAsync("Win32_LogicalDisk.DeviceID=\"C:\"", cancellationToken: ct));
        Assert.Contains(all, o => o.SystemProperties.ClassName == "Win32_LogicalDiskRootDirectory");
        Assert.Contains(all, o => o.SystemProperties.ClassName == "Win32_LogicalDiskToPartition");

        var roots = await ToListAsync(wmi.GetReferencesAsync("Win32_LogicalDisk.DeviceID=\"C:\"",
            resultClass: "Win32_LogicalDiskRootDirectory", role: "GroupComponent", cancellationToken: ct));
        var root = Assert.Single(roots);
        Assert.Equal(["GroupComponent", "PartComponent"], root.KeyNames.Order());
        var disk = Assert.IsType<WmiInstance>(root["GroupComponent"]);
        Assert.Equal("Win32_LogicalDisk", disk.SystemProperties.ClassName);
        Assert.Equal("C:", disk["DeviceID"]);
        Assert.Equal("Win32_Directory", Assert.IsType<WmiInstance>(root["PartComponent"]).SystemProperties.ClassName);

        Assert.Empty(await ToListAsync(wmi.GetReferencesAsync("Win32_LogicalDisk.DeviceID=\"C:\"",
            resultClass: "Win32_LogicalDiskRootDirectory", role: "PartComponent", cancellationToken: ct)));
    }
}
