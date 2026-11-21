using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;

namespace NWmi.Transport;

internal sealed record DnsSrvEntry(ushort Priority, ushort Weight, ushort Port, string Target);

/// <summary>
/// The lookups Kerberos discovery needs (SRV, PTR, A/AAAA), done by the DnsClient.NET package against a given list of
/// servers or, if none is given, the ones the machine's network adapters use. .NET has no SRV lookup of its own and the
/// system resolver cannot be told to ask a particular server. A server that does not answer is skipped for the next;
/// a name that does not exist is an empty answer.
/// </summary>
internal sealed class DnsResolver
{
    public const int DefaultPort = 53;

    private readonly ILookupClient _client;
    private readonly ILogger _logger;

    private DnsResolver(ILookupClient client, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _logger = logger;
    }

    public static DnsResolver Create(IEnumerable<IPAddress>? servers, ILogger logger) =>
        Create(servers?.Select(a => new IPEndPoint(a, DefaultPort)).ToArray(), TimeSpan.FromSeconds(2), logger);

    public static DnsResolver Create(IReadOnlyList<IPEndPoint>? servers, TimeSpan timeout, ILogger logger)
    {
        var options = servers is { Count: > 0 }
            ? new LookupClientOptions([.. servers])
            : new LookupClientOptions();
        options.Timeout = timeout;
        options.Retries = 1;
        options.UseCache = false;
        options.UseRandomNameServer = false; // the order given is the order tried
        options.ContinueOnDnsError = true;
        options.ThrowDnsErrors = false;
        return new DnsResolver(new LookupClient(options), logger);
    }

    public async Task<IReadOnlyList<DnsSrvEntry>> QuerySrvAsync(string name,
        CancellationToken cancellationToken = default)
    {
        var answers = await QueryAsync(name, QueryType.SRV, cancellationToken).ConfigureAwait(false);
        return
        [
            .. answers.OfType<SrvRecord>()
                .Select(r => new DnsSrvEntry(r.Priority, r.Weight, r.Port, TrimDot(r.Target.Value)))
        ];
    }

    public async Task<IReadOnlyList<string>> QueryPtrAsync(IPAddress address,
        CancellationToken cancellationToken = default)
    {
        var answers = await QueryAsync(address.GetArpaName(), QueryType.PTR, cancellationToken).ConfigureAwait(false);
        return [.. answers.OfType<PtrRecord>().Select(r => TrimDot(r.PtrDomainName.Value))];
    }

    public async Task<IReadOnlyList<IPAddress>> QueryAddressesAsync(string name,
        CancellationToken cancellationToken = default)
    {
        var a = await QueryAsync(name, QueryType.A, cancellationToken).ConfigureAwait(false);
        var aaaa = await QueryAsync(name, QueryType.AAAA, cancellationToken).ConfigureAwait(false);
        return [.. a.OfType<AddressRecord>().Concat(aaaa.OfType<AddressRecord>()).Select(r => r.Address)];
    }

    private async Task<IReadOnlyList<DnsResourceRecord>> QueryAsync(string name, QueryType type,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.QueryAsync(name, type, QueryClass.IN, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("DNS {Type} {Name}: {Count} answers.", type, name, response.Answers.Count);
            return [.. response.Answers];
        }
        catch (Exception ex) when
            (ex is DnsResponseException or System.Net.Sockets.SocketException or ArgumentException)
        {
            _logger.LogDebug(ex, "DNS {Type} {Name} failed.", type, name);
            return [];
        }
    }

    private static string TrimDot(string name) => name.TrimEnd('.');
}
