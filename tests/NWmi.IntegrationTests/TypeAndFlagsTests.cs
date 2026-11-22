using NWmi.Options;

namespace NWmi.IntegrationTests;

/// <summary>Property types and flags as a real server's classes give them, over DCOM and (where the host has a listener) WinRM.</summary>
public class TypeAndFlagsTests
{
    private static async Task<WmiConnection> ConnectAsync(bool winRm)
    {
        var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
        if (!winRm)
            return await WmiConnection.ConnectAsync(IntegrationSettings.Host!, credential);

        await IntegrationSettings.RequireWinRmAsync();
        return await WmiConnection.ConnectWinRmAsync(IntegrationSettings.Host!, credential);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_process_instance_has_its_declared_types_and_flags(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var ct = TestContext.Current.CancellationToken;

        WmiInstance? process = null;
        await foreach (var o in wmi.QueryAsync("SELECT * FROM Win32_Process WHERE ProcessId = 4", cancellationToken: ct))
            process = o;

        Assert.NotNull(process);
        var handle = process.Properties["Handle"]!;
        Assert.Equal(WmiType.String, handle.Type);
        Assert.True(handle.Flags.HasFlag(WmiFlags.Property | WmiFlags.Key | WmiFlags.NotModified));
        Assert.Equal(WmiType.UInt32, process.Properties["ProcessId"]!.Type);
        Assert.Equal(WmiType.DateTime, process.Properties["CreationDate"]!.Type);
        Assert.Equal(WmiType.UInt64, process.Properties["KernelModeTime"]!.Type);
        // The System process has no executable path.
        Assert.True(process.Properties["ExecutablePath"]!.Flags.HasFlag(WmiFlags.NullValue));

        Assert.True(handle.Flags.HasFlag(WmiFlags.ReadOnly));
    }

    [IntegrationFact]
    public async Task Class_qualifiers_give_read_only_required_and_reference_types()
    {
        await using var wmi = await ConnectAsync(false);
        var ct = TestContext.Current.CancellationToken;

        var environment = await wmi.GetClassAsync("Win32_Environment", cancellationToken: ct);
        Assert.NotNull(environment);
        // Name is key, read and write; UserName is key and read only (as MMI reports them).
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.NullValue, environment.Properties["Name"]!.Flags);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NullValue,
            environment.Properties["UserName"]!.Flags);
        Assert.Equal(WmiType.Boolean, environment.Properties["SystemVariable"]!.Type);

        var association = await wmi.GetClassAsync("Win32_SystemServices", cancellationToken: ct);
        Assert.NotNull(association);
        Assert.Equal(WmiType.Reference, association.Properties["PartComponent"]!.Type);

        var indication = await wmi.GetClassAsync("CIM_ClassCreation", cancellationToken: ct);
        Assert.NotNull(indication);
        Assert.True(indication.Properties["ClassDefinition"]!.Flags.HasFlag(WmiFlags.Required));
        Assert.Equal(WmiType.Instance, indication.Properties["ClassDefinition"]!.Type);

        var creation = await wmi.GetClassAsync("__InstanceCreationEvent", cancellationToken: ct);
        Assert.NotNull(creation);
        Assert.Equal(WmiType.UInt8Array, creation.Properties["SECURITY_DESCRIPTOR"]!.Type);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Method_output_parameters_are_typed(bool winRm)
    {
        await using var wmi = await ConnectAsync(winRm);
        var ct = TestContext.Current.CancellationToken;

        // StdRegProv.GetStringValue on a value that is there: HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion ProductName.
        var output = await wmi.InvokeMethodAsync("StdRegProv", "GetStringValue",
            new Dictionary<string, object?>
            {
                ["hDefKey"] = 0x80000002u,
                ["sSubKeyName"] = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                ["sValueName"] = "ProductName",
            }, cancellationToken: ct);

        Assert.NotNull(output);
        var returnValue = output.Properties["ReturnValue"]!;
        Assert.Equal(WmiType.UInt32, returnValue.Type);
        Assert.True(returnValue.Flags.HasFlag(WmiFlags.Parameter));
        Assert.Equal(WmiType.String, output.Properties["sValue"]!.Type);
        Assert.True(returnValue.Flags.HasFlag(WmiFlags.Out));
    }
}
