using System.Net;
using System.Runtime.Versioning;

namespace NWmi.Auth;

/// <summary>
/// The account a Kerberos client signs in as: a username, realm, and password, or the ticket-granting ticket already
/// held in a credential cache file (<see cref="FromTicketCache"/>).
/// </summary>
public sealed class KerberosCredential
{
    private readonly NetworkCredential? _credential;

    private KerberosCredential(string? ticketCachePath, string realm, bool usesLogonSession)
    {
        TicketCachePath = ticketCachePath;
        UsesLogonSession = usesLogonSession;
        Realm = realm;
    }

    /// <summary>
    /// A credential that signs in with the ticket-granting ticket in an MIT-format credential cache file (<c>KRB5CCNAME</c>,
    /// as written by <c>kinit</c> or Kerberos.NET), so no password is needed. The file is read once and never written to.
    /// Service tickets are still requested from the KDC. NTLM cannot be offered with such a credential.
    /// </summary>
    /// <param name="path">The credential cache file.</param>
    /// <param name="realm">The realm of the ticket-granting ticket (e.g. <c>AD.CONTOSO.COM</c>).</param>
    public static KerberosCredential FromTicketCache(string path, string realm)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(realm);
        return new KerberosCredential(path, realm, false);
    }

    /// <summary>Creates a credential for <c>userName</c> in <c>realm</c> (e.g. <c>AD.CONTOSO.COM</c>).</summary>
    public KerberosCredential(string userName, string password, string realm)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrEmpty(realm);
        _credential = new NetworkCredential(userName, password, realm);
        Realm = realm;
    }

    /// <summary>
    /// A credential for the account Windows has logged in on: the service ticket is obtained through the operating
    /// system's own ticket cache (LSA), using the ticket-granting ticket of the current logon session, so no password
    /// or KDC address is needed. Windows only, and the machine must be able to reach a domain controller (a domain
    /// member, or a workstation with a domain logon). NTLM cannot be offered with such a credential.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static KerberosCredential FromLogonSession() => new(null, string.Empty, usesLogonSession: true);

    /// <summary>The account name, without the realm.</summary>
    public string UserName => _credential?.UserName ?? string.Empty;

    /// <summary>The Kerberos realm the account belongs to.</summary>
    public string Realm { get; }

    /// <summary>Whether the service ticket comes from the Windows logon session (see <see cref="FromLogonSession"/>).</summary>
    public bool UsesLogonSession { get; }

    /// <summary>The credential cache file, or null for a password credential.</summary>
    public string? TicketCachePath { get; }

    internal string Password => _credential?.Password
                                ?? throw new InvalidOperationException(
                                    "This credential has no password: it signs in with a ticket cache.");
}
