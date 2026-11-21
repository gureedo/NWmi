using System.Net;

namespace NWmi.Auth;

/// <summary>A username, password and domain to authenticate with.</summary>
public sealed class Credential
{
    private readonly NetworkCredential _credential;

    /// <summary>Creates a credential</summary>
    public Credential(string userName, string password, string domain = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(domain);
        _credential = new NetworkCredential(userName, password, domain);
    }

    /// <summary>The account name, without a domain.</summary>
    public string UserName => _credential.UserName;

    /// <summary>The domain name.</summary>
    public string Domain => _credential.Domain;

    /// <summary>The account password.</summary>
    public string Password => _credential.Password;

    /// <summary>Splits "DOMAIN\user" or "user@domain" into its parts; a bare name has an empty domain.</summary>
    public static Credential Parse(string account, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        var slash = account.IndexOf('\\', StringComparison.Ordinal);
        if (slash >= 0)
            return new Credential(account[(slash + 1)..], password, account[..slash]);

        var at = account.IndexOf('@', StringComparison.Ordinal);
        return at > 0
            ? new Credential(account[..at], password, account[(at + 1)..])
            : new Credential(account, password);
    }
}
