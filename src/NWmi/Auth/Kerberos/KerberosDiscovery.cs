using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NWmi.Transport;

namespace NWmi.Auth.Kerberos;

/// <summary>
/// Fills in what <see cref="SpnegoOptions"/> leaves out, the way Windows' own Negotiate package does: the realm from the
/// account (or, failing that, the target's DNS domain), the KDC from the realm's <c>_kerberos._tcp</c> SRV records, and
/// the SPN as <c>host/&lt;fqdn&gt;</c> of the target. Anything the options state outright is used as is.
/// </summary>
internal static class KerberosDiscovery
{
    internal sealed record KerberosTarget(
        string Realm,
        IReadOnlyList<(string Host, int Port)> Kdcs,
        string ServicePrincipalName);

    public static Task<KerberosTarget?> ResolveAsync(string? host, Credential credential, SpnegoOptions options,
        CancellationToken cancellationToken, DnsResolver? dnsClient = null, int ldapPort = KerberosConstants.LdapPort,
        ILogger? logger = null) =>
        ResolveAsync(host, credential.Domain, needKdc: true, options, cancellationToken, dnsClient, ldapPort, logger);

    public static async Task<KerberosTarget?> ResolveAsync(string? host, string domain, bool needKdc,
        SpnegoOptions options, CancellationToken cancellationToken, DnsResolver? dnsClient = null,
        int ldapPort = KerberosConstants.LdapPort, ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var dns = dnsClient ?? DnsResolver.Create(options.DnsServers, log);

        var spn = options.ServicePrincipalName;
        string? fqdn = null;
        if (spn is null)
        {
            fqdn = host is null ? null : await ResolveFqdnAsync(dns, host, cancellationToken).ConfigureAwait(false);
            if (fqdn is null)
            {
                log.LogDebug("Kerberos: cannot tell the DNS name of {Host}; offering NTLM only.", host ?? "the target");
                return null;
            }

            spn = $"host/{fqdn}";
        }

        if (!needKdc)
            return new KerberosTarget((options.Realm ?? domain).ToUpperInvariant(), [], spn);

        var realm = options.Realm;
        if (string.IsNullOrEmpty(realm) && domain.Contains('.', StringComparison.Ordinal))
            realm = domain; // user@realm, or a DNS-style domain

        var suffixes = DomainSuffixes(fqdn ?? spn[(spn.IndexOf('/', StringComparison.Ordinal) + 1)..]);
        if (string.IsNullOrEmpty(realm) && domain.Length > 0)
        {
            // A NetBIOS domain (CONTOSO): ask the target's domain controllers which DNS domain carries that name.
            var lookup = await DcLocator.FindRealmAsync(dns, domain, suffixes, cancellationToken, ldapPort)
                .ConfigureAwait(false);
            switch (lookup.Status)
            {
                case RealmLookupStatus.Found:
                    realm = lookup.Realm;
                    log.LogDebug("Kerberos: a domain controller says {Domain} is {Realm}.", domain, realm);
                    break;
                case RealmLookupStatus.NotFound:
                    if (log.IsEnabled(LogLevel.Debug))
                        log.LogDebug("Kerberos: no domain of {Suffixes} is called {Domain}; offering NTLM only.",
                            string.Join(", ", suffixes), domain);

                    return null;
            }
        }

        IReadOnlyList<DnsSrvEntry> srv = [];
        if (string.IsNullOrEmpty(realm))
        {
            // Nothing could confirm a domain: the nearest suffix of the target's DNS name that has a KDC.
            foreach (var candidate in suffixes)
            {
                srv = await dns.QuerySrvAsync($"_kerberos._tcp.{candidate}", cancellationToken).ConfigureAwait(false);
                if (srv.Count > 0)
                {
                    realm = candidate;
                    break;
                }
            }

            if (string.IsNullOrEmpty(realm))
            {
                log.LogDebug("Kerberos: no realm given and none found from the target's DNS name; offering NTLM only.");
                return null;
            }
        }

        var kdcs = new List<(string Host, int Port)>();
        if (!string.IsNullOrEmpty(options.KdcHost))
            kdcs.Add((options.KdcHost, options.KdcPort));
        else
        {
            if (srv.Count == 0)
                srv = await dns.QuerySrvAsync($"_kerberos._tcp.{realm}", cancellationToken).ConfigureAwait(false);

            // RFC 2782: lowest priority first, then higher weight first (a full weighted shuffle buys nothing here).
            foreach (var record in srv.OrderBy(r => r.Priority).ThenByDescending(r => r.Weight))
            {
                var addresses = await dns.QueryAddressesAsync(record.Target, cancellationToken).ConfigureAwait(false);
                kdcs.Add((addresses.Count > 0 ? addresses[0].ToString() : record.Target, record.Port));
            }

            if (kdcs.Count == 0)
            {
                log.LogDebug("Kerberos: no KDC found for {Realm}; offering NTLM only.", realm);
                return null;
            }
        }

        return new KerberosTarget(realm.ToUpperInvariant(), kdcs, spn);
    }

    private static IReadOnlyList<string> DomainSuffixes(string fqdn)
    {
        var labels = fqdn.Split('.');
        return [.. Enumerable.Range(1, Math.Max(0, labels.Length - 2)).Select(i => string.Join('.', labels[i..]))];
    }

    /// <summary>
    /// The target's fully qualified DNS name: a name with dots is taken as it is, an address is looked up in the
    /// reverse zone, a bare host name is canonicalized by the system resolver.
    /// </summary>
    private static async Task<string?> ResolveFqdnAsync(DnsResolver dns, string host,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            var names = await dns.QueryPtrAsync(address, cancellationToken).ConfigureAwait(false);
            return names.Count > 0 ? names[0] : null;
        }

        if (host.Contains('.', StringComparison.Ordinal))
            return host.TrimEnd('.');

        try
        {
            var entry = await Dns.GetHostEntryAsync(host, cancellationToken).ConfigureAwait(false);
            return entry.HostName.Contains('.', StringComparison.Ordinal) ? entry.HostName.TrimEnd('.') : null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}
