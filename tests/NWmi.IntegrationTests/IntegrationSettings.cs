using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace NWmi.IntegrationTests;

/// <summary>Opt-in settings: tests are skipped unless NWMI_TEST_HOST is set.</summary>
internal static class IntegrationSettings
{
    public static string? Host => Environment.GetEnvironmentVariable("NWMI_TEST_HOST");
    public static string? User => Environment.GetEnvironmentVariable("NWMI_TEST_USER");
    public static string? Password => Environment.GetEnvironmentVariable("NWMI_TEST_PASS");

    public static bool Configured => !string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(User);

    /// <summary>Skips the test on a host with no WinRM listener, such as the workgroup Windows 11 VM.</summary>
    public static async Task RequireWinRmAsync(int port = 5985)
    {
        using var probe = new TcpClient();
        try
        {
            await probe.ConnectAsync(Host!, port, TestContext.Current.CancellationToken);
        }
        catch (SocketException)
        {
            Assert.Skip($"Needs a WinRM listener on port {port}.");
        }
    }
}

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute([CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IntegrationSettings.Configured)
            Skip = "Set NWMI_TEST_HOST, NWMI_TEST_USER and NWMI_TEST_PASS to run.";
    }
}

public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    public IntegrationTheoryAttribute([CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IntegrationSettings.Configured)
            Skip = "Set NWMI_TEST_HOST, NWMI_TEST_USER and NWMI_TEST_PASS to run.";
    }
}
