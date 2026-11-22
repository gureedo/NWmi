using NWmi.Auth;
using NWmi.Client;

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

        WmiObject? process = null;
        await foreach (var o in wmi.QueryAsync("SELECT * FROM Win32_Process WHERE ProcessId = 4", cancellationToken: ct))
            process = o;

        Assert.NotNull(process);
        var handle = process.GetProperty("Handle");
        Assert.Equal(WmiType.String, handle.Type);
        Assert.True(handle.Flags.HasFlag(WmiFlags.Property | WmiFlags.Key | WmiFlags.NotModified));
        Assert.Equal(WmiType.UInt32, process.GetProperty("ProcessId").Type);
        Assert.Equal(WmiType.DateTime, process.GetProperty("CreationDate").Type);
        Assert.Equal(WmiType.UInt64, process.GetProperty("KernelModeTime").Type);
        // The System process has no executable path.
        Assert.True(process.GetProperty("ExecutablePath").Flags.HasFlag(WmiFlags.NullValue));

        // WinRM does not read the class's qualifiers, so read-only is only known over DCOM.
        Assert.Equal(!winRm, handle.Flags.HasFlag(WmiFlags.ReadOnly));
    }

    [IntegrationFact]
    public async Task Class_qualifiers_give_read_only_required_and_reference_types()
    {
        await using var wmi = await ConnectAsync(false);
        var ct = TestContext.Current.CancellationToken;

        var environment = await wmi.GetObjectAsync("Win32_Environment", cancellationToken: ct);
        Assert.NotNull(environment);
        // Name is key, read and write; UserName is key and read only (as MMI reports them).
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.NullValue, environment.GetProperty("Name").Flags);
        Assert.Equal(WmiFlags.Property | WmiFlags.Key | WmiFlags.ReadOnly | WmiFlags.NullValue,
            environment.GetProperty("UserName").Flags);
        Assert.Equal(WmiType.Boolean, environment.GetProperty("SystemVariable").Type);

        var association = await wmi.GetObjectAsync("Win32_SystemServices", cancellationToken: ct);
        Assert.NotNull(association);
        Assert.Equal(WmiType.Reference, association.GetProperty("PartComponent").Type);

        var indication = await wmi.GetObjectAsync("CIM_ClassCreation", cancellationToken: ct);
        Assert.NotNull(indication);
        Assert.True(indication.GetProperty("ClassDefinition").Flags.HasFlag(WmiFlags.Required));
        Assert.Equal(WmiType.Instance, indication.GetProperty("ClassDefinition").Type);

        var creation = await wmi.GetObjectAsync("__InstanceCreationEvent", cancellationToken: ct);
        Assert.NotNull(creation);
        Assert.Equal(WmiType.UInt8Array, creation.GetProperty("SECURITY_DESCRIPTOR").Type);
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
        var returnValue = output.GetProperty("ReturnValue");
        Assert.Equal(WmiType.UInt32, returnValue.Type);
        Assert.True(returnValue.Flags.HasFlag(WmiFlags.Parameter));
        Assert.Equal(WmiType.String, output.GetProperty("sValue").Type);
        if (!winRm)
            Assert.True(returnValue.Flags.HasFlag(WmiFlags.Out));
    }
}
