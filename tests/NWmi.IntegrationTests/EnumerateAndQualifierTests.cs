using NWmi.Options;

namespace NWmi.IntegrationTests;

public class EnumerateAndQualifierTests
{
    private static Task<WmiConnection> ConnectAsync(string ns = @"root\cimv2") =>
        WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = ns });

    private static async Task<List<WmiInstance>> Collect(IAsyncEnumerable<WmiInstance> source)
    {
        var list = new List<WmiInstance>();
        await foreach (var o in source)
            list.Add(o);

        return list;
    }

    [IntegrationFact]
    public async Task EnumerateInstances_returns_the_same_instances_as_a_SELECT()
    {
        await using var wmi = await ConnectAsync();

        var enumerated = await Collect(wmi.EnumerateInstancesAsync("Win32_LogicalDisk"));
        var queried = await Collect(wmi.QueryAsync("SELECT * FROM Win32_LogicalDisk"));

        Assert.NotEmpty(enumerated);
        Assert.Equal(
            queried.Select(d => d.SystemProperties.Path).Order(StringComparer.OrdinalIgnoreCase),
            enumerated.Select(d => d.SystemProperties.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(enumerated, d => Assert.Equal("Win32_LogicalDisk", d.SystemProperties.ClassName));
    }

    [IntegrationFact]
    public async Task EnumerateInstances_shallow_leaves_out_the_instances_of_subclasses()
    {
        await using var wmi = await ConnectAsync();

        // CIM_LogicalDisk is abstract: its instances are all of subclasses such as Win32_LogicalDisk.
        var deep = await Collect(wmi.EnumerateInstancesAsync("CIM_LogicalDisk"));
        var shallow = await Collect(wmi.EnumerateInstancesAsync("CIM_LogicalDisk", shallow: true));

        Assert.Contains(deep, d => d.SystemProperties.ClassName == "Win32_LogicalDisk");
        Assert.Empty(shallow);
    }

    [IntegrationFact]
    public async Task EnumerateInstances_of_a_missing_class_throws()
    {
        await using var wmi = await ConnectAsync();

        await Assert.ThrowsAsync<WmiException>(() => Collect(wmi.EnumerateInstancesAsync("NoSuchClass_nwmi")));
    }

    [IntegrationFact]
    public async Task A_property_qualifier_set_on_an_instance_goes_through_PutInstance()
    {
        await using var wmi = await ConnectAsync(@"root\default");
        var path = "__Namespace.Name=\"nwmiqualtest\"";
        try
        {
            var template = new WmiInstance(await wmi.GetClassAsync("__Namespace"));
            Assert.NotNull(template);
            template["Name"] = "nwmiqualtest";
            template.SetPropertyQualifier("Name", "NWmiNote", "hello");
            await wmi.PutInstanceAsync(template, WbemChangeFlags.CreateOnly);

            var read = await wmi.GetInstanceAsync(path);
            Assert.NotNull(read);
            Assert.Equal("nwmiqualtest", read["Name"]);
            var qualifiers = read.GetPropertyQualifiers("Name");
            Assert.True(qualifiers.TryGetValue("NWmiNote", out var note), "qualifiers: " + string.Join(", ", qualifiers.Keys));
            Assert.Equal("hello", note);
        }
        finally
        {
            try
            {
                await wmi.DeleteInstanceAsync(path);
            }
            catch (WmiException)
            {
            }
        }
    }
}
