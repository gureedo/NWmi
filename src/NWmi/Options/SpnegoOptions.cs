using System.Net;
using Microsoft.Extensions.Logging;
using NWmi.Auth.Kerberos;

namespace NWmi.Options;

/// <summary>
/// Where and how SPNEGO gets a Kerberos ticket. Every setting is optional: what is left out is worked out the way Windows
/// does (the realm from the account or the target's DNS domain, the KDC from the realm's SRV records, the SPN as
/// <c>host/&lt;fqdn&gt;</c>), and if that fails only NTLM is offered.
/// </summary>
public sealed class SpnegoOptions
{
    /// <summary>
    /// The Kerberos realm, e.g. <c>AD.CONTOSO.COM</c>. Defaults to the credential's domain when that is a DNS name
    /// (<c>user@ad.contoso.com</c>); for a NetBIOS domain (<c>CONTOSO</c>) a domain controller of the target's DNS
    /// domain is asked what that domain is called (an LDAP ping, UDP 389), and without an answer the target's own DNS
    /// domain is assumed.
    /// </summary>
    public string? Realm { get; set; }

    /// <summary>The KDC's address. Defaults to the realm's <c>_kerberos._tcp</c> SRV records.</summary>
    public string? KdcHost { get; set; }

    /// <summary>The KDC port. Defaults to 88.</summary>
    public int KdcPort { get; set; } = KerberosConstants.KdcPort;

    /// <summary>The target's service principal name. Defaults to <c>host/&lt;fqdn&gt;</c> of the target.</summary>
    public string? ServicePrincipalName { get; set; }

    /// <summary>The DNS servers to ask for SRV and PTR records. Defaults to the ones the machine's network adapters use.</summary>
    public IReadOnlyList<IPAddress>? DnsServers { get; set; }

    /// <summary>Settings for the NTLM mechanism (workstation name, target name).</summary>
    public NtlmSecurityOptions? Ntlm { get; set; }

    /// <summary>Where the negotiation's diagnostic messages (and Kerberos.NET's) are logged. Defaults to no logging.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>A copy to fill in for one connection, so the caller's instance can be shared between connections.</summary>
    internal SpnegoOptions Copy(ILoggerFactory? fallbackLoggerFactory) => new()
    {
        Realm = Realm,
        KdcHost = KdcHost,
        KdcPort = KdcPort,
        ServicePrincipalName = ServicePrincipalName,
        DnsServers = DnsServers,
        Ntlm = Ntlm?.Copy(),
        LoggerFactory = LoggerFactory ?? fallbackLoggerFactory,
    };
}
