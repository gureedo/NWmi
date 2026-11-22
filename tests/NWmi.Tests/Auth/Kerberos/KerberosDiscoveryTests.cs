using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NWmi.Auth.Kerberos;
using NWmi.Options;
using NWmi.Transport;

namespace NWmi.Tests.Auth.Kerberos;

public class KerberosDiscoveryTests
{
    private static readonly Credential Upn = new("alice", "pw", "ad.contoso.com");
    private static readonly Credential NetBios = new("alice", "pw", "CONTOSO");

    private static FakeDnsServer Zone()
    {
        var dns = new FakeDnsServer();
        dns.Srv["_kerberos._tcp.ad.contoso.com"] =
        [
            (1, 100, 88, "dc02.ad.contoso.com"),
            (0, 10, 88, "dc01.ad.contoso.com"),
            (0, 90, 8888, "dc03.ad.contoso.com"),
        ];
        dns.A["dc01.ad.contoso.com"] = IPAddress.Parse("10.0.0.10");
        dns.A["dc02.ad.contoso.com"] = IPAddress.Parse("10.0.0.11");
        dns.Ptr["20.0.0.10.in-addr.arpa"] = "w1.ad.contoso.com";
        return dns;
    }

    [Fact]
    public async Task Dns_client_reads_srv_records_with_compressed_targets()
    {
        await using var dns = Zone();

        var records = await dns.Client().QuerySrvAsync("_kerberos._tcp.ad.contoso.com", TestContext.Current.CancellationToken);

        Assert.Equal(3, records.Count);
        Assert.Contains(new DnsSrvEntry(0, 10, 88, "dc01.ad.contoso.com"), records);
        Assert.Contains(new DnsSrvEntry(0, 90, 8888, "dc03.ad.contoso.com"), records);
    }

    [Fact]
    public async Task Dns_client_reads_ptr_and_address_records_and_treats_a_missing_name_as_empty()
    {
        await using var dns = Zone();
        var client = dns.Client();

        Assert.Equal(["w1.ad.contoso.com"], await client.QueryPtrAsync(IPAddress.Parse("10.0.0.20"), TestContext.Current.CancellationToken));
        Assert.Equal([IPAddress.Parse("10.0.0.10")], await client.QueryAddressesAsync("dc01.ad.contoso.com", TestContext.Current.CancellationToken));
        Assert.Empty(await client.QueryAddressesAsync("nobody.ad.contoso.com", TestContext.Current.CancellationToken));
        Assert.Empty(await client.QuerySrvAsync("_kerberos._tcp.other.com", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dns_client_moves_on_from_a_server_that_does_not_answer()
    {
        await using var dns = Zone();
        using var silent = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var client = DnsResolver.Create([(IPEndPoint)silent.Client.LocalEndPoint!, dns.EndPoint], TimeSpan.FromMilliseconds(300), NullLogger.Instance);

        Assert.Single(await client.QueryPtrAsync(IPAddress.Parse("10.0.0.20"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_name_and_a_upn_account_give_the_realm_the_best_kdc_and_the_spn()
    {
        await using var dns = Zone();

        var target = await KerberosDiscovery.ResolveAsync(
            "w1.ad.contoso.com", Upn, new SpnegoOptions(), TestContext.Current.CancellationToken, dns.Client());

        Assert.NotNull(target);
        Assert.Equal("AD.CONTOSO.COM", target.Realm);
        Assert.Equal("host/w1.ad.contoso.com", target.ServicePrincipalName);
        // Priority 0 first, higher weight first; the SRV record's own port is kept; unresolvable targets stay names.
        Assert.Equal([("dc03.ad.contoso.com", 8888), ("10.0.0.10", 88), ("10.0.0.11", 88)], target.Kdcs.Select(k => (k.Host, k.Port)).ToArray());
    }

    [Fact]
    public async Task An_address_is_turned_into_the_spn_through_the_reverse_zone()
    {
        await using var dns = Zone();

        var target = await KerberosDiscovery.ResolveAsync(
            "10.0.0.20", Upn, new SpnegoOptions(), TestContext.Current.CancellationToken, dns.Client());

        Assert.Equal("host/w1.ad.contoso.com", target!.ServicePrincipalName);
    }

    [Fact]
    public async Task A_netbios_domain_falls_back_to_the_targets_dns_domain_for_the_realm()
    {
        await using var dns = Zone();

        var target = await KerberosDiscovery.ResolveAsync(
            "w1.ad.contoso.com", NetBios, new SpnegoOptions(), TestContext.Current.CancellationToken, dns.Client());

        Assert.Equal("AD.CONTOSO.COM", target!.Realm);
    }

    [Fact]
    public async Task The_realm_search_walks_up_the_targets_domain_to_the_first_suffix_with_a_kdc()
    {
        await using var dns = Zone();
        dns.Srv.Remove("_kerberos._tcp.sub.ad.contoso.com");

        var target = await KerberosDiscovery.ResolveAsync(
            "w9.sub.ad.contoso.com", NetBios, new SpnegoOptions(), TestContext.Current.CancellationToken, dns.Client());

        Assert.Equal("AD.CONTOSO.COM", target!.Realm);
        Assert.Contains("33 _kerberos._tcp.sub.ad.contoso.com", dns.Queries);
    }

    [Fact]
    public async Task Settings_given_in_the_options_are_used_without_asking_dns()
    {
        await using var dns = Zone();
        var options = new SpnegoOptions
        {
            Realm = "other.example",
            KdcHost = "10.9.9.9",
            KdcPort = 1088,
            ServicePrincipalName = "RPCSS/w7.other.example",
        };

        var target = await KerberosDiscovery.ResolveAsync("10.0.0.20", NetBios, options, TestContext.Current.CancellationToken, dns.Client());

        Assert.Equal("OTHER.EXAMPLE", target!.Realm);
        Assert.Equal("RPCSS/w7.other.example", target.ServicePrincipalName);
        Assert.Equal([("10.9.9.9", 1088)], target.Kdcs.Select(k => (k.Host, k.Port)).ToArray());
        Assert.Empty(dns.Queries);
    }

    [Fact]
    public async Task Kerberos_is_skipped_with_a_reason_when_nothing_can_be_found()
    {
        await using var dns = Zone();
        var messages = new List<string>();
        var options = new SpnegoOptions();
        var logger = new ListLogger(messages);

        // An address with no reverse record, a name in a zone with no KDC, and no target at all.
        Assert.Null(await KerberosDiscovery.ResolveAsync("10.0.0.99", Upn, options, TestContext.Current.CancellationToken, dns.Client(), logger: logger));
        Assert.Null(await KerberosDiscovery.ResolveAsync("host.nowhere.example", NetBios, options, TestContext.Current.CancellationToken, dns.Client(), logger: logger));
        Assert.Null(await KerberosDiscovery.ResolveAsync(null, Upn, options, TestContext.Current.CancellationToken, dns.Client(), logger: logger));

        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Contains("offering NTLM only", m, StringComparison.Ordinal));
    }
}
