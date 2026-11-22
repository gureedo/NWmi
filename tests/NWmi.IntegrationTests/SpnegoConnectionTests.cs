using System.Net;
using NWmi.Options;

namespace NWmi.IntegrationTests;

/// <summary>
/// SPNEGO on a single connection. NTLM needs only the usual NWMI_TEST_* settings (NWMI_TEST_USER may be
/// <c>DOMAIN\user</c>); Kerberos is added when NWMI_TEST_REALM, NWMI_TEST_KDC and NWMI_TEST_SPN are set too.
/// </summary>
public class SpnegoConnectionTests
{
    private static string? Realm => Environment.GetEnvironmentVariable("NWMI_TEST_REALM");

    private static string? Kdc => Environment.GetEnvironmentVariable("NWMI_TEST_KDC");

    private static string? Spn => Environment.GetEnvironmentVariable("NWMI_TEST_SPN");

    private static bool KerberosConfigured => !string.IsNullOrEmpty(Realm) && !string.IsNullOrEmpty(Kdc) && !string.IsNullOrEmpty(Spn);

    private static async Task<string?> OperatingSystemAsync(SpnegoOptions options, List<string> log)
    {
        options.LoggerFactory = new ListLoggerFactory(log);
        await using var wmi = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            options);

        await foreach (var row in wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem"))
            return row["Caption"] as string;

        return null;
    }

    [IntegrationFact]
    public async Task Ntlm_through_spnego_queries_the_host()
    {
        var log = new List<string>();

        Assert.NotNull(await OperatingSystemAsync(new SpnegoOptions(), log));
        Assert.Contains(log, m => m.Contains("server mechListMIC verified", StringComparison.Ordinal));
    }

    [IntegrationFact]
    public async Task Kerberos_is_offered_first_and_completes_without_further_legs_or_falls_back_in_place()
    {
        if (!KerberosConfigured)
            Assert.Skip("Set NWMI_TEST_REALM, NWMI_TEST_KDC and NWMI_TEST_SPN to run.");

        var log = new List<string>();
        var options = new SpnegoOptions { Realm = Realm, KdcHost = Kdc, ServicePrincipalName = Spn };

        Assert.NotNull(await OperatingSystemAsync(options, log));
        Assert.Contains(log, m => m.StartsWith("SPNEGO: offering", StringComparison.Ordinal) && m.Contains("optimistic 1.2.840.113554.1.2.2", StringComparison.Ordinal));
    }

    [IntegrationFact]
    public async Task Everything_is_discovered_from_the_host_address_and_the_account()
    {
        // NWMI_TEST_DNS: a DNS server for the AD zone (the domain controller). With a NetBIOS domain in the user name
        // and a reverse record for the host, the realm, KDC and SPN all come from DNS and LDAP pings.
        var dns = Environment.GetEnvironmentVariable("NWMI_TEST_DNS");
        if (string.IsNullOrEmpty(dns) || !IPAddress.TryParse(IntegrationSettings.Host, out _))
            Assert.Skip("Set NWMI_TEST_DNS and use an IP address as NWMI_TEST_HOST (with a PTR record) to run.");

        var log = new List<string>();

        Assert.NotNull(await OperatingSystemAsync(new SpnegoOptions { DnsServers = [IPAddress.Parse(dns)] }, log));
        Assert.Contains(log, m => m.StartsWith("Kerberos: ticket for host/", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("optimistic 1.2.840.113554.1.2.2", StringComparison.Ordinal));
    }

    [IntegrationFact]
    public async Task A_ticket_cache_file_signs_in_without_a_password()
    {
        if (!KerberosConfigured)
            Assert.Skip("Set NWMI_TEST_REALM, NWMI_TEST_KDC and NWMI_TEST_SPN to run.");

        // Write a credential cache the way kinit would: log in with the password once, keeping only the TGT file.
        var path = Path.Combine(Path.GetTempPath(), $"nwmi-ccache-{Guid.NewGuid():N}");
        try
        {
            var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
            var realm = Realm!.ToUpperInvariant();
            var config = Kerberos.NET.Configuration.Krb5Config.Parse(string.Join('\n',
                "[libdefaults]", $" default_realm = {realm}", "[realms]", $" {realm} = {{", $"  kdc = {Kdc}:88", " }", string.Empty));
            using (var client = new Kerberos.NET.Client.KerberosClient(config))
            {
                client.CacheInMemory = false;
                client.Cache = new Kerberos.NET.Client.Krb5TicketCache(path) { PersistChanges = true };
                await client.Authenticate(new Kerberos.NET.Credentials.KerberosPasswordCredential(
                    credential.UserName, credential.Password, realm));
            }

            Assert.True(File.Exists(path), "the credential cache was not written");
            var before = File.ReadAllBytes(path);

            await using var wmi = await WmiConnection.ConnectAsync(
                IntegrationSettings.Host!,
                KerberosCredential.FromTicketCache(path, Realm),
                Kdc!,
                Spn!);

            await foreach (var row in wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem"))
            {
                Assert.NotNull(row["Caption"]);
                break;
            }

            Assert.Equal(before, File.ReadAllBytes(path)); // the file is only read
        }
        finally
        {
            File.Delete(path);
        }
    }

    [IntegrationFact]
    public async Task A_cancelled_call_is_orphaned_and_the_next_connection_authenticates_with_kerberos_again()
    {
        if (!KerberosConfigured)
            Assert.Skip("Set NWMI_TEST_REALM, NWMI_TEST_KDC and NWMI_TEST_SPN to run.");

        var log = new List<string>();
        var spnego = new SpnegoOptions { Realm = Realm, KdcHost = Kdc, ServicePrincipalName = Spn, LoggerFactory = new ListLoggerFactory(log) };
        await using var wmi = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!),
            spnego,
            new WmiConnectionOptions { Timeout = TimeSpan.FromSeconds(15) });
        static int Offers(List<string> log) => log.ToArray().Count(m => m.StartsWith("SPNEGO: offering", StringComparison.Ordinal));
        var before = Offers(log);

        const string neverFires =
            "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = 'nwmi-never.exe'";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in wmi.WatchAsync(neverFires, cancellationToken: cancel.Token))
            {
            }
        });

        await foreach (var row in wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem"))
        {
            Assert.NotNull(row["Caption"]);
            break;
        }

        Assert.True(Offers(log) > before, "the connection opened after the cancelled call did not authenticate again");
    }

    [IntegrationFact]
    public async Task A_ticket_cache_finds_the_spn_and_kdc_through_dns()
    {
        var dns = Environment.GetEnvironmentVariable("NWMI_TEST_DNS");
        if (!KerberosConfigured || string.IsNullOrEmpty(dns) || !IPAddress.TryParse(IntegrationSettings.Host, out _))
            Assert.Skip("Set NWMI_TEST_REALM, NWMI_TEST_KDC, NWMI_TEST_SPN and NWMI_TEST_DNS, and use an IP address as NWMI_TEST_HOST.");

        var path = Path.Combine(Path.GetTempPath(), $"nwmi-ccache-{Guid.NewGuid():N}");
        try
        {
            var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
            var realm = Realm!.ToUpperInvariant();
            var config = Kerberos.NET.Configuration.Krb5Config.Parse(string.Join('\n',
                "[libdefaults]", $" default_realm = {realm}", "[realms]", $" {realm} = {{", $"  kdc = {Kdc}:88", " }", string.Empty));
            using (var client = new Kerberos.NET.Client.KerberosClient(config))
            {
                client.CacheInMemory = false;
                client.Cache = new Kerberos.NET.Client.Krb5TicketCache(path) { PersistChanges = true };
                await client.Authenticate(new Kerberos.NET.Credentials.KerberosPasswordCredential(
                    credential.UserName, credential.Password, realm));
            }

            var log = new List<string>();
            await using var wmi = await WmiConnection.ConnectAsync(
                IntegrationSettings.Host!,
                KerberosCredential.FromTicketCache(path, realm),
                new SpnegoOptions { DnsServers = [IPAddress.Parse(dns)], LoggerFactory = new ListLoggerFactory(log) });

            await foreach (var row in wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem"))
            {
                Assert.NotNull(row["Caption"]);
                break;
            }

            Assert.Contains(log, m => m.Contains("optimistic 1.2.840.113554.1.2.2", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [IntegrationFact]
    public async Task The_windows_logon_session_signs_in_without_a_password()
    {
        // Run from a domain-joined Windows machine, signed in to the domain: NWMI_TEST_SPN is the target's SPN.
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Spn)
            || Environment.GetEnvironmentVariable("NWMI_TEST_LOGON_SESSION") != "1")
        {
            Assert.Skip("Needs Windows with a domain logon, NWMI_TEST_SPN and NWMI_TEST_LOGON_SESSION=1.");
            return;
        }

        await using var wmi = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!, KerberosCredential.FromLogonSession(), Spn);

        await foreach (var row in wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem"))
        {
            Assert.NotNull(row["Caption"]);
            break;
        }
    }

    [IntegrationTheory]
    [InlineData(RpcAuthLevel.PacketPrivacy)]
    [InlineData(RpcAuthLevel.PacketIntegrity)]
    public async Task Kerberos_only_works_at_both_protection_levels_with_header_signing(RpcAuthLevel level)
    {
        if (!KerberosConfigured)
            Assert.Skip("Set NWMI_TEST_REALM, NWMI_TEST_KDC and NWMI_TEST_SPN to run.");

        var credential = Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);
        await using var wmi = await WmiConnection.ConnectAsync(
            IntegrationSettings.Host!,
            new KerberosCredential(credential.UserName, credential.Password, Realm!),
            Kdc!,
            Spn!,
            new WmiConnectionOptions { AuthLevel = level });

        await foreach (var row in wmi.QueryAsync("SELECT Caption FROM Win32_OperatingSystem"))
        {
            Assert.NotNull(row["Caption"]);
            return;
        }

        Assert.Fail("no operating system returned");
    }
}
