using NWmi.Auth;

namespace NWmi.Tests.Auth.Kerberos;

public class KerberosCredentialTests
{
    [Fact]
    public void A_ticket_cache_credential_has_a_realm_and_path_but_no_password()
    {
        var credential = KerberosCredential.FromTicketCache("/tmp/krb5cc_1000", "AD.CONTOSO.COM");

        Assert.Equal("/tmp/krb5cc_1000", credential.TicketCachePath);
        Assert.Equal("AD.CONTOSO.COM", credential.Realm);
        Assert.Equal(string.Empty, credential.UserName);
    }

    [Fact]
    public void A_password_credential_has_no_ticket_cache() =>
        Assert.Null(new KerberosCredential("alice", "pw", "AD.CONTOSO.COM").TicketCachePath);

    [Fact]
    public async Task A_missing_ticket_cache_file_is_reported_as_a_login_failure()
    {
        var credential = KerberosCredential.FromTicketCache(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "EXAMPLE.COM");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => NWmi.Client.WmiConnection.ConnectAsync(
            "127.0.0.1", credential, "127.0.0.1", "host/server01.example.com",
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(ex is SecurityProviderException or FileNotFoundException, ex.GetType().Name);
    }

    [Fact]
    public void A_logon_session_credential_has_neither_password_nor_cache_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The logon session credential is Windows only.");
            return;
        }

        var credential = KerberosCredential.FromLogonSession();

        Assert.True(credential.UsesLogonSession);
        Assert.Null(credential.TicketCachePath);
        Assert.False(new KerberosCredential("alice", "pw", "AD.CONTOSO.COM").UsesLogonSession);
    }
}
