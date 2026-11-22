using System.Globalization;
using NWmi.Options;

namespace NWmi.IntegrationTests;

/// <summary>The 32-bit and 64-bit registry views of <c>StdRegProv</c>, chosen through <see cref="WmiCallContext"/>.</summary>
public class CallContextTests
{
    private const uint HklmKey = 0x80000002;
    private const string Native = @"SOFTWARE\NWmiArchTest";
    private const string Redirected = @"SOFTWARE\WOW6432Node\NWmiArchTest";

    private static readonly WmiCallContext Arch64 =
        WmiCallContext.Empty.With("__ProviderArchitecture", 64).With("__RequiredArchitecture", true);

    private static readonly WmiCallContext Arch32 =
        WmiCallContext.Empty.With("__ProviderArchitecture", 32).With("__RequiredArchitecture", true);

    private static Task<WmiConnection> ConnectAsync() =>
        WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = @"root\default" });

    private static async Task<int> Reg(WmiConnection wmi, string method, string subKey, string? valueName = null, string? value = null)
    {
        var parameters = new Dictionary<string, object?> { ["hDefKey"] = HklmKey, ["sSubKeyName"] = subKey };
        if (valueName is not null)
            parameters["sValueName"] = valueName;

        if (value is not null)
            parameters["sValue"] = value;

        var result = await wmi.InvokeMethodAsync("StdRegProv", method, parameters);
        Assert.NotNull(result);
        return Convert.ToInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadString(WmiConnection wmi, string subKey)
    {
        var parameters = new Dictionary<string, object?> { ["hDefKey"] = HklmKey, ["sSubKeyName"] = subKey, ["sValueName"] = "V" };
        var result = await wmi.InvokeMethodAsync("StdRegProv", "GetStringValue", parameters);
        Assert.NotNull(result);
        return Convert.ToInt32(result["ReturnValue"], CultureInfo.InvariantCulture) == 0 ? (string?)result["sValue"] : null;
    }

    [IntegrationFact]
    public async Task The_32_and_64_bit_registry_views_are_told_apart_by_the_context()
    {
        await using var wmi = await ConnectAsync();
        try
        {
            // Written through the 64-bit view, a key is at its real path and is not visible at the same path in the 32-bit view.
            wmi.Context = Arch64;
            Assert.Equal(0, await Reg(wmi, "CreateKey", Native));
            Assert.Equal(0, await Reg(wmi, "SetStringValue", Native, "V", "native"));
            Assert.Equal("native", await ReadString(wmi, Native));
            Assert.Null(await ReadString(wmi, Redirected)); // the 64-bit view does not redirect

            wmi.Context = Arch32;
            Assert.Null(await ReadString(wmi, Native)); // redirected to WOW6432Node, where there is nothing yet

            // Written through the 32-bit view, a key lands under WOW6432Node.
            Assert.Equal(0, await Reg(wmi, "CreateKey", Native));
            Assert.Equal(0, await Reg(wmi, "SetStringValue", Native, "V", "wow"));
            Assert.Equal("wow", await ReadString(wmi, Native));

            wmi.Context = Arch64;
            Assert.Equal("native", await ReadString(wmi, Native));
            Assert.Equal("wow", await ReadString(wmi, Redirected));
        }
        finally
        {
            wmi.Context = Arch64;
            await Reg(wmi, "DeleteKey", Native);
            await Reg(wmi, "DeleteKey", Redirected);
        }
    }

    [IntegrationFact]
    public async Task A_namespace_opened_from_a_connection_starts_with_its_context()
    {
        await using var root = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            new WmiConnectionOptions { Namespace = "root", Context = Arch64 });
        await using var child = await root.OpenNamespaceAsync("default");

        Assert.Equal(64, child.Context!.Values["__ProviderArchitecture"]);
        try
        {
            Assert.Equal(0, await Reg(child, "CreateKey", Native));
            Assert.Equal(0, await Reg(child, "SetStringValue", Native, "V", "native"));
            Assert.Null(await ReadString(child, Redirected));
        }
        finally
        {
            await Reg(child, "DeleteKey", Native);
        }
    }
}
