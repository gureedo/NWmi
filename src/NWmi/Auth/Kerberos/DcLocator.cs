using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DnsClient;
using NWmi.Transport;

namespace NWmi.Auth.Kerberos;

internal enum RealmLookupStatus
{
    Found,
    NotFound,
    Unknown,
}

/// <summary>
/// A small domain controller locator, enough to turn a NetBIOS domain name (<c>CONTOSO</c>) into the realm
/// (<c>AD.CONTOSO.COM</c>) the way Windows' DC locator does: ask a domain controller over connectionless LDAP (UDP 389)
/// for its NetLogon attribute (MS-ADTS 6.3.3.2 "LDAP ping", MS-NRPC 2.2.1.2 NETLOGON_SAM_LOGON_RESPONSE_EX), which names
/// the domain both ways.
/// </summary>
internal static class DcLocator
{
    internal enum SearchScope
    {
        BaseObject = 0,
    }

    internal enum DerefAliases
    {
        Never = 0,
    }

    internal sealed record DomainInfo(
        string DnsDomainName,
        string NetBiosDomainName,
        string DnsForestName,
        string DnsHostName);

    internal sealed record RealmLookup(RealmLookupStatus Status, string? Realm = null);

    private static readonly Asn1Tag SearchRequestTag = new(TagClass.Application, 3, isConstructed: true);
    private static readonly Asn1Tag SearchResultEntryTag = new(TagClass.Application, 4, isConstructed: true);
    private static readonly Asn1Tag AndFilterTag = new(TagClass.ContextSpecific, 0, isConstructed: true);
    private static readonly Asn1Tag EqualityMatchTag = new(TagClass.ContextSpecific, 3, isConstructed: true);
    private static readonly TimeSpan DefaultPingTimeout = TimeSpan.FromSeconds(2);

    public static async Task<RealmLookup> FindRealmAsync(DnsResolver dns, string netBiosDomain,
        IEnumerable<string> candidateDomains, CancellationToken cancellationToken, int ldapPort = KerberosConstants.LdapPort,
        TimeSpan? timeout = null)
    {
        var answered = false;
        foreach (var domain in candidateDomains)
        {
            var controllers = await dns.QuerySrvAsync($"_ldap._tcp.dc._msdcs.{domain}", cancellationToken)
                .ConfigureAwait(false);

            foreach (var controller in controllers.OrderBy(c => c.Priority).ThenByDescending(c => c.Weight))
            {
                var addresses = await dns.QueryAddressesAsync(controller.Target, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var address in addresses)
                {
                    var info = await LdapPingAsync(address, domain, ldapPort, timeout ?? DefaultPingTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (info is null)
                        continue;

                    answered = true;
                    if (string.Equals(info.NetBiosDomainName, netBiosDomain, StringComparison.OrdinalIgnoreCase))
                        return new RealmLookup(RealmLookupStatus.Found, info.DnsDomainName);

                    goto nextDomain; // one controller speaks for its domain
                }
            }

            nextDomain: ;
        }

        return new RealmLookup(answered ? RealmLookupStatus.NotFound : RealmLookupStatus.Unknown);
    }

    internal static async Task<DomainInfo?> LdapPingAsync(IPAddress controller, string dnsDomain, int port,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var socket = new Socket(controller.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await socket.ConnectAsync(new IPEndPoint(controller, port), cts.Token).ConfigureAwait(false);
            await socket.SendAsync(BuildSearchRequest(dnsDomain), cts.Token).ConfigureAwait(false);

            // A server may send the entry and the "done" message in separate datagrams; take the first entry.
            var buffer = new byte[4096];
            while (true)
            {
                var length = await socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                var info = ParseSearchResult(buffer.AsSpan(0, length));
                if (info is not null)
                    return info;
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// searchRequest(base "", baseObject, filter (&amp;(DnsDomain=…)(NtVer=…)), attributes NetLogon), as a connectionless LDAP
    /// message (RFC 1798) with message id 0.
    /// </summary>
    internal static byte[] BuildSearchRequest(string dnsDomain)
    {
        // NETLOGON_NT_VERSION_5 | NETLOGON_NT_VERSION_5EX: the reply format with DNS names (MS-ADTS 6.3.1.1).
        const uint netLogonNtVersion = 0x00000006;

        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(0);
            using (writer.PushSequence(SearchRequestTag))
            {
                writer.WriteOctetString([]); // baseObject
                writer.WriteEnumeratedValue(SearchScope.BaseObject);
                writer.WriteEnumeratedValue(DerefAliases.Never);
                writer.WriteInteger(0); // sizeLimit
                writer.WriteInteger(0); // timeLimit
                writer.WriteBoolean(false); // typesOnly
                using (writer.PushSequence(AndFilterTag))
                {
                    WriteEquality(writer, "DnsDomain", Encoding.UTF8.GetBytes(dnsDomain));
                    Span<byte> ntVersion = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(ntVersion, netLogonNtVersion);
                    WriteEquality(writer, "NtVer", ntVersion);
                }

                using (writer.PushSequence())
                    writer.WriteOctetString("Netlogon"u8);
            }
        }

        return writer.Encode();
    }

    private static void WriteEquality(AsnWriter writer, string attribute, ReadOnlySpan<byte> value)
    {
        using (writer.PushSequence(EqualityMatchTag))
        {
            writer.WriteOctetString(Encoding.ASCII.GetBytes(attribute));
            writer.WriteOctetString(value);
        }
    }

    internal static DomainInfo? ParseSearchResult(ReadOnlySpan<byte> datagram)
    {
        try
        {
            var message = new AsnReader(datagram.ToArray(), AsnEncodingRules.BER).ReadSequence();
            message.ReadInteger();
            if (!message.PeekTag().HasSameClassAndValue(SearchResultEntryTag))
                return null;

            var entry = message.ReadSequence(SearchResultEntryTag);
            entry.ReadOctetString(); // objectName
            var attributes = entry.ReadSequence();
            while (attributes.HasData)
            {
                var attribute = attributes.ReadSequence();
                var type = Encoding.ASCII.GetString(attribute.ReadOctetString());
                var values = attribute.ReadSetOf();
                if (type.Equals("netlogon", StringComparison.OrdinalIgnoreCase) && values.HasData)
                    return ParseNetLogon(values.ReadOctetString());
            }

            return null;
        }
        catch (Exception ex) when (ex is AsnContentException or FormatException or IndexOutOfRangeException
                                       or ArgumentOutOfRangeException or DnsResponseParseException)
        {
            return null;
        }
    }

    /// <summary>NETLOGON_SAM_LOGON_RESPONSE_EX (MS-NRPC 2.2.1.2): the names use DNS-style compression, offsets from the start of the blob.</summary>
    internal static DomainInfo? ParseNetLogon(byte[] blob)
    {
        const ushort responseEx = 23; // LOGON_SAM_LOGON_RESPONSE_EX
        if (blob.Length < 25 || BinaryPrimitives.ReadUInt16LittleEndian(blob) != responseEx)
            return null;

        const int namesOffset = 2 + 2 + 4 + 16; // opcode, sbz, flags, domain GUID
        var reader = new DnsDatagramReader(new ArraySegment<byte>(blob), namesOffset);
        var forest = reader.ReadDnsName().Value.TrimEnd('.');
        var domain = reader.ReadDnsName().Value.TrimEnd('.');
        var host = reader.ReadDnsName().Value.TrimEnd('.');
        var netBios = reader.ReadDnsName().Value.TrimEnd('.');
        return new DomainInfo(domain, netBios, forest, host);
    }
}
