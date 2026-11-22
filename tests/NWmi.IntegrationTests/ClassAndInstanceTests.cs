using NWmi.Options;

namespace NWmi.IntegrationTests;

/// <summary>Classes and instances as a real server gives them, over DCOM and (where the host has a listener) WinRM.</summary>
public class ClassAndInstanceTests
{
    private static async Task<WmiConnection> ConnectAsync(bool winRm)
    {
        var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
        if (!winRm)
            return await WmiConnection.ConnectAsync(IntegrationSettings.Host!, credential);

        await IntegrationSettings.RequireWinRmAsync();
        return await WmiConnection.ConnectWinRmAsync(IntegrationSettings.Host!, credential);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);

        return list;
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_class_has_its_properties_methods_and_qualifiers(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var ct = TestContext.Current.CancellationToken;

        var process = await wmi.GetClassAsync("Win32_Process", cancellationToken: ct);

        Assert.Equal("Win32_Process", process.Name);
        Assert.Equal("CIM_Process", process.SuperClassName);
        Assert.Equal(@"root\cimv2", process.SystemProperties.Namespace, ignoreCase: true);
        Assert.Contains(process.Qualifiers, q => q.Name == "provider" && (string?)q.Value == "CIMWin32");
        Assert.DoesNotContain(process.Qualifiers, q => q.Name == "CIMTYPE");

        // As MMI reports Win32_Process.Handle: String; Property, Key, ReadOnly, NullValue; key, MaxLen and read.
        var handle = process.Properties["Handle"]!;
        Assert.Equal(WmiType.String, handle.Type);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NullValue, handle.Flags);
        Assert.Equal(["key", "MaxLen", "read"], handle.Qualifiers.Select(q => q.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(WmiFlags.DisableOverride | WmiFlags.ToSubclass, handle.Qualifiers["key"]!.Flags);

        Assert.Equal(
            ["AttachDebugger", "Create", "GetAvailableVirtualSize", "GetOwner", "GetOwnerSid", "SetPriority", "Terminate"],
            process.Methods.Select(m => m.Name).Order());
        var create = process.Methods["Create"]!;
        Assert.Equal(WmiType.UInt32, create.ReturnType);
        Assert.Contains(create.Qualifiers, q => q.Name == "Static" && q.Value is true);
        Assert.Equal(["CommandLine", "CurrentDirectory", "ProcessStartupInformation", "ProcessId"],
            create.Parameters.Select(p => p.Name));
        Assert.Equal(WmiType.Instance, create.Parameters["ProcessStartupInformation"]!.Type);
        Assert.Equal(WmiFlags.Parameter | WmiFlags.In, create.Parameters["CommandLine"]!.Flags);
        Assert.Equal(WmiFlags.Parameter | WmiFlags.Out, create.Parameters["ProcessId"]!.Flags);
        Assert.Equal(3, create.Parameters["ProcessId"]!.Qualifiers["ID"]!.Value);

        var logon = await wmi.GetClassAsync("Win32_LoggedOnUser", cancellationToken: ct);
        Assert.Equal(WmiType.Reference, logon.Properties["Antecedent"]!.Type);
        Assert.Equal("Win32_Account", logon.Properties["Antecedent"]!.ReferenceClassName);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_instance_carries_its_class_and_tracks_its_changes(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var ct = TestContext.Current.CancellationToken;

        var system = await wmi.GetInstanceAsync("Win32_Process.Handle=\"4\"", cancellationToken: ct);

        Assert.Equal("Win32_Process", system.Class.Name);
        Assert.Equal("Win32_Process", system.SystemProperties.ClassName);
        Assert.Equal("Win32_Process.Handle=\"4\"", system.SystemProperties.RelativePath);
        Assert.True(system.Class.Properties["Handle"]!.Flags.HasFlag(WmiFlags.Key));
        // The binary encoding of an instance leaves its class's methods out; the WinRM class definition has them.
        Assert.Equal(winRm, system.Class.Methods.Count > 0);

        var name = system.Properties["Name"]!;
        Assert.False(name.IsValueModified);
        system["Name"] = "renamed";
        Assert.True(name.IsValueModified);
        Assert.False(system.Properties["Handle"]!.IsValueModified);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_class_that_a_query_returns_is_an_instance_with_no_path(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);

        var rows = await ToListAsync(wmi.QueryAsync("SELECT * FROM meta_class WHERE __CLASS = 'Win32_Process'",
            cancellationToken: TestContext.Current.CancellationToken));

        var row = Assert.Single(rows);
        Assert.Equal("Win32_Process", row.SystemProperties.ClassName);
        Assert.Null(row.SystemProperties.Path);
        Assert.All(row.Properties, p => Assert.True(p.Flags.HasFlag(WmiFlags.NullValue | WmiFlags.NotModified)));
        Assert.Equal("Win32_Process", row.Class.Name);
        Assert.NotNull(row.Class.Properties["Handle"]);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_instance_or_class_is_not_found(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var ct = TestContext.Current.CancellationToken;

        var instance = await Assert.ThrowsAsync<WmiException>(() =>
            wmi.GetInstanceAsync("Win32_Process.Handle=\"999999999\"", cancellationToken: ct));
        Assert.Equal(unchecked((int)WbemStatus.NotFound), instance.HResult);

        var @class = await Assert.ThrowsAsync<WmiException>(() => wmi.GetClassAsync("NWmi_NoSuchClass", cancellationToken: ct));
        Assert.Equal(unchecked((int)WbemStatus.NotFound), @class.HResult);

        await Assert.ThrowsAsync<ArgumentException>(() => wmi.GetInstanceAsync("Win32_Process", cancellationToken: ct));
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Class_enumeration_gives_full_classes_deep_or_shallow(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var ct = TestContext.Current.CancellationToken;

        var deep = await ToListAsync(wmi.EnumerateClassesAsync("CIM_LogicalDevice", cancellationToken: ct));
        var shallow = await ToListAsync(wmi.EnumerateClassesAsync("CIM_LogicalDevice", shallow: true, cancellationToken: ct));
        var top = await ToListAsync(wmi.EnumerateClassesAsync(shallow: true, cancellationToken: ct));

        Assert.Contains(deep, c => c.Name == "Win32_Processor");
        Assert.DoesNotContain(deep, c => c.Name == "CIM_LogicalDevice");
        Assert.All(shallow, c => Assert.Equal("CIM_LogicalDevice", c.SuperClassName));
        Assert.Contains(shallow, c => c.Name == "CIM_Processor");
        Assert.All(top, c => Assert.Null(c.SuperClassName));
        Assert.Contains(top, c => c.Name == "CIM_ManagedSystemElement");

        var processor = deep.Single(c => c.Name == "Win32_Processor");
        Assert.True(processor.Properties["DeviceID"]!.Flags.HasFlag(WmiFlags.Key));
        Assert.NotEmpty(processor.Qualifiers);
    }

    [IntegrationFact]
    public async Task Both_transports_describe_a_class_the_same_way()
    {
        await using var dcom = await ConnectAsync(false);
        await using var winRm = await ConnectAsync(true);
        var ct = TestContext.Current.CancellationToken;

        foreach (var name in new[] { "Win32_Process", "Win32_Service", "StdRegProv", "Win32_LogicalDisk" })
        {
            var a = await dcom.GetClassAsync(name, cancellationToken: ct);
            var b = await winRm.GetClassAsync(name, cancellationToken: ct);

            Assert.Equal(a.SuperClassName, b.SuperClassName);
            Assert.Equal(a.Properties.Select(Describe).Order(), b.Properties.Select(Describe).Order());
            Assert.Equal(a.Methods.Select(DescribeMethod).Order(), b.Methods.Select(DescribeMethod).Order());
        }

        static string Describe(WmiPropertyDeclaration p) => $"{p.Name}:{p.Type}:{p.Flags}:{p.ReferenceClassName}";

        static string DescribeMethod(WmiMethodDeclaration m) =>
            $"{m.Name}:{m.ReturnType}({string.Join(",", m.Parameters.Select(p => $"{p.Name}:{p.Type}:{p.Flags}"))})";
    }
}
