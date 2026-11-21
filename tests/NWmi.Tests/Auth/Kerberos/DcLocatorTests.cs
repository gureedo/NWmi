using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NWmi.Auth;
using NWmi.Auth.Kerberos;

namespace NWmi.Tests.Auth.Kerberos;

/// <summary>A UDP LDAP "ping" responder: answers a Netlogon search for a DNS domain it knows, stays silent for any other.</summary>
internal sealed class FakeLdapPingServer : IAsyncDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeLdapPingServer()
    {
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Domain controller replies by the DnsDomain the request asks about: (forest, domain, host, NetBIOS domain).</summary>
    public Dictionary<string, (string Forest, string Domain, string Host, string Netbios)> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string DnsDomain, uint NtVer)> Requests { get; } = [];

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _udp.Dispose();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var received = await _udp.ReceiveAsync(_stop.Token);
            var (domain, ntVer) = ReadRequest(received.Buffer);
            Requests.Add((domain, ntVer));
            if (!Domains.TryGetValue(domain, out var d))
                continue;

            await _udp.SendAsync(BuildEntry(NetlogonBlob.Build(d.Forest, d.Domain, d.Host, d.Netbios)), received.RemoteEndPoint, _stop.Token);
            await _udp.SendAsync(BuildDone(), received.RemoteEndPoint, _stop.Token); // the entry is not the only datagram
        }
    }

    /// <summary>Reads the DnsDomain and NtVer assertions out of the request, checking its shape on the way.</summary>
    private static (string DnsDomain, uint NtVer) ReadRequest(byte[] datagram)
    {
        var message = new AsnReader(datagram, AsnEncodingRules.BER).ReadSequence();
        message.ReadInteger();
        var search = message.ReadSequence(new Asn1Tag(TagClass.Application, 3, isConstructed: true));
        Assert.Empty(search.ReadOctetString()); // base object ""
        Assert.Equal(new byte[] { 0 }, search.ReadEnumeratedBytes().ToArray()); // scope: baseObject
        search.ReadEnumeratedBytes(); // derefAliases
        search.ReadInteger();
        search.ReadInteger();
        search.ReadBoolean();
        var filter = search.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        string? domain = null;
        uint ntVer = 0;
        while (filter.HasData)
        {
            var equality = filter.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3, isConstructed: true));
            var attribute = Encoding.ASCII.GetString(equality.ReadOctetString());
            var value = equality.ReadOctetString();
            if (attribute == "DnsDomain")
                domain = Encoding.UTF8.GetString(value);
            else if (attribute == "NtVer")
                ntVer = BinaryPrimitives.ReadUInt32LittleEndian(value);
        }

        Assert.Equal("Netlogon", Encoding.ASCII.GetString(search.ReadSequence().ReadOctetString()));
        return (domain!, ntVer);
    }

    private static byte[] BuildEntry(byte[] netlogon)
    {
        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(0);
            using (writer.PushSequence(new Asn1Tag(TagClass.Application, 4, isConstructed: true)))
            {
                writer.WriteOctetString([]);
                using (writer.PushSequence())
                using (writer.PushSequence())
                {
                    writer.WriteOctetString("Netlogon"u8);
                    using (writer.PushSetOf()) writer.WriteOctetString(netlogon);
                }
            }
        }

        return writer.Encode();
    }

    private static byte[] BuildDone()
    {
        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(0);
            using (writer.PushSequence(new Asn1Tag(TagClass.Application, 5, isConstructed: true)))
            {
                writer.WriteEnumeratedValue(LdapResult.Success);
                writer.WriteOctetString([]);
                writer.WriteOctetString([]);
            }
        }

        return writer.Encode();
    }
}

/// <summary>NETLOGON_SAM_LOGON_RESPONSE_EX with DNS-style name compression, offsets from the start of the blob.</summary>
internal enum LdapResult
{
    Success = 0,
}

internal static class NetlogonBlob
{
    public static byte[] Build(string forest, string domain, string host, string netbios)
    {
        var blob = new List<byte>();
        blob.AddRange(BitConverter.GetBytes((ushort)23)); // LOGON_SAM_LOGON_RESPONSE_EX
        blob.AddRange(new byte[2]); // sbz
        blob.AddRange(BitConverter.GetBytes(0x000003FDu)); // flags
        blob.AddRange(new byte[16]); // domain GUID

        var forestAt = blob.Count;
        WriteName(blob, forest, null);
        var domainAt = blob.Count;
        // "ad.contoso.com" after "contoso.com": the extra labels, then a pointer to the forest name.
        WriteName(blob, domain, domain.EndsWith("." + forest, StringComparison.OrdinalIgnoreCase) ? (forest, forestAt) : null);
        WriteName(blob, host, (domain, domainAt));
        WriteName(blob, netbios, null);
        WriteName(blob, host.Split('.')[0].ToUpperInvariant(), null); // NetBIOS computer name
        WriteName(blob, "Administrator", null); // user name
        WriteName(blob, "Default-First-Site-Name", null);
        WriteName(blob, "Default-First-Site-Name", null);
        blob.AddRange(BitConverter.GetBytes(0x00000006u)); // NtVersion
        blob.AddRange(new byte[4]); // tokens
        return [.. blob];
    }

    private static void WriteName(List<byte> blob, string name, (string Suffix, int At)? pointTo)
    {
        var labels = name.Split('.');
        var keep = labels.Length;
        if (pointTo is { } p && name.EndsWith("." + p.Suffix, StringComparison.OrdinalIgnoreCase))
            keep = labels.Length - p.Suffix.Split('.').Length;

        foreach (var label in labels.Take(keep))
        {
            blob.Add((byte)label.Length);
            blob.AddRange(Encoding.ASCII.GetBytes(label));
        }

        if (keep < labels.Length)
        {
            blob.Add((byte)(0xC0 | (pointTo!.Value.At >> 8)));
            blob.Add((byte)pointTo.Value.At);
        }
        else
            blob.Add(0);
    }
}

public class DcLocatorTests
{
    private static async Task<(FakeDnsServer Dns, FakeLdapPingServer Ldap)> LabAsync()
    {
        var dns = new FakeDnsServer();
        // The controller of ad.contoso.com is reached through DNS: SRV for the locator name, then its address.
        dns.Srv["_ldap._tcp.dc._msdcs.ad.contoso.com"] = [(0, 100, 389, "dc01.ad.contoso.com")];
        dns.A["dc01.ad.contoso.com"] = IPAddress.Loopback;
        var ldap = new FakeLdapPingServer();
        ldap.Domains["ad.contoso.com"] = ("contoso.com", "ad.contoso.com", "dc01.ad.contoso.com", "CONTOSO");
        await Task.CompletedTask;
        return (dns, ldap);
    }

    [Fact]
    public void The_ping_request_is_a_connectionless_ldap_search_for_the_netlogon_attribute()
    {
        var request = DcLocator.BuildSearchRequest("ad.contoso.com");

        var message = new AsnReader(request, AsnEncodingRules.BER).ReadSequence();
        Assert.Equal(0, (int)message.ReadInteger()); // message id
        Assert.True(message.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.Application, 3, isConstructed: true)));
    }

    [Fact]
    public void A_netlogon_blob_with_compressed_names_is_read()
    {
        var info = DcLocator.ParseNetLogon(NetlogonBlob.Build("contoso.com", "ad.contoso.com", "dc01.ad.contoso.com", "CONTOSO"));

        Assert.Equal(new DcLocator.DomainInfo("ad.contoso.com", "CONTOSO", "contoso.com", "dc01.ad.contoso.com"), info);
    }

    [Fact]
    public void Something_that_is_not_a_netlogon_response_is_ignored()
    {
        Assert.Null(DcLocator.ParseNetLogon([1, 2, 3]));
        Assert.Null(DcLocator.ParseNetLogon(new byte[40])); // wrong opcode
        Assert.Null(DcLocator.ParseSearchResult([0x30, 0x03, 0x02, 0x01, 0x00]));
    }

    [Fact]
    public async Task A_controller_ping_returns_the_domain_names()
    {
        await using var ldap = (await LabAsync()).Ldap;

        var info = await DcLocator.LdapPingAsync(IPAddress.Loopback, "ad.contoso.com", ldap.Port, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal("CONTOSO", info!.NetBiosDomainName);
        Assert.Equal("ad.contoso.com", info.DnsDomainName);
        Assert.Equal(("ad.contoso.com", 6u), ldap.Requests.Single());
    }

    [Fact]
    public async Task A_controller_that_does_not_answer_gives_nothing()
    {
        await using var ldap = (await LabAsync()).Ldap;

        Assert.Null(await DcLocator.LdapPingAsync(IPAddress.Loopback, "other.example", ldap.Port, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_netbios_domain_is_found_among_the_candidate_domains_nearest_first()
    {
        var (dns, ldap) = await LabAsync();
        await using var _ = dns;
        await using var __ = ldap;

        // "sub.ad.contoso.com" has no controllers in DNS; ad.contoso.com is next.
        var lookup = await DcLocator.FindRealmAsync(dns.Client(), "contoso", ["sub.ad.contoso.com", "ad.contoso.com", "contoso.com"],
            TestContext.Current.CancellationToken, ldap.Port, TimeSpan.FromSeconds(2));

        Assert.Equal(new DcLocator.RealmLookup(RealmLookupStatus.Found, "ad.contoso.com"), lookup);
    }

    [Fact]
    public async Task A_netbios_name_no_candidate_domain_has_is_not_found_but_unreachable_controllers_are_unknown()
    {
        var (dns, ldap) = await LabAsync();
        await using var _ = dns;
        await using var __ = ldap;

        var notFound = await DcLocator.FindRealmAsync(dns.Client(), "FABRIKAM", ["ad.contoso.com"], TestContext.Current.CancellationToken,
            ldap.Port, TimeSpan.FromSeconds(2));
        var unknownDomain = await DcLocator.FindRealmAsync(dns.Client(), "CONTOSO", ["nowhere.example"], TestContext.Current.CancellationToken,
            ldap.Port, TimeSpan.FromSeconds(2));
        var silent = await DcLocator.FindRealmAsync(dns.Client(), "CONTOSO", ["ad.contoso.com"], TestContext.Current.CancellationToken,
            ldap.Port + 1, TimeSpan.FromMilliseconds(300)); // nothing listens there

        Assert.Equal(RealmLookupStatus.NotFound, notFound.Status);
        Assert.Equal(RealmLookupStatus.Unknown, unknownDomain.Status);
        Assert.Equal(RealmLookupStatus.Unknown, silent.Status);
    }

    [Fact]
    public async Task Discovery_maps_a_netbios_domain_to_the_realm_through_a_controller()
    {
        var (dns, ldap) = await LabAsync();
        await using var _ = dns;
        await using var __ = ldap;
        dns.Srv["_kerberos._tcp.ad.contoso.com"] = [(0, 100, 88, "dc01.ad.contoso.com")];

        var target = await KerberosDiscovery.ResolveAsync("w1.ad.contoso.com", new Credential("alice", "pw", "CONTOSO"), new SpnegoOptions(),
            TestContext.Current.CancellationToken, dns.Client(), ldap.Port);

        Assert.Equal("AD.CONTOSO.COM", target!.Realm);
        Assert.Equal("host/w1.ad.contoso.com", target.ServicePrincipalName);
    }

    [Fact]
    public async Task Discovery_offers_ntlm_only_when_controllers_say_the_netbios_domain_is_not_theirs()
    {
        var (dns, ldap) = await LabAsync();
        await using var _ = dns;
        await using var __ = ldap;
        dns.Srv["_kerberos._tcp.ad.contoso.com"] = [(0, 100, 88, "dc01.ad.contoso.com")];
        var messages = new List<string>();

        var target = await KerberosDiscovery.ResolveAsync("w1.ad.contoso.com", new Credential("alice", "pw", "WIN11PRO"),
            new SpnegoOptions(), TestContext.Current.CancellationToken, dns.Client(), ldap.Port, new ListLogger(messages));

        Assert.Null(target); // not a guess from the DNS name
        Assert.Contains(messages, m => m.Contains("is called WIN11PRO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discovery_falls_back_to_the_dns_name_when_no_controller_can_be_pinged()
    {
        var (dns, ldap) = await LabAsync();
        await using var _ = dns;
        await using var __ = ldap;
        dns.Srv["_kerberos._tcp.ad.contoso.com"] = [(0, 100, 88, "dc01.ad.contoso.com")];

        var target = await KerberosDiscovery.ResolveAsync("w1.ad.contoso.com", new Credential("alice", "pw", "CONTOSO"), new SpnegoOptions(),
            TestContext.Current.CancellationToken, dns.Client(), ldap.Port + 1);

        Assert.Equal("AD.CONTOSO.COM", target!.Realm);
    }
}
