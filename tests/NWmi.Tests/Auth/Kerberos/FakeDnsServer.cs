using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NWmi.Transport;

namespace NWmi.Tests.Auth.Kerberos;

/// <summary>A UDP DNS server on loopback that answers SRV, PTR and A queries from a table; anything else is NXDOMAIN.</summary>
internal sealed class FakeDnsServer : IAsyncDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeDnsServer()
    {
        _loop = Task.Run(RunAsync);
    }

    public Dictionary<string, List<(ushort Priority, ushort Weight, ushort Port, string Target)>> Srv { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Ptr { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, IPAddress> A { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Queries { get; } = [];

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public DnsResolver Client() => DnsResolver.Create([EndPoint], TimeSpan.FromSeconds(2), NullLogger.Instance);

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
            var query = received.Buffer;
            var position = 12;
            var labels = new List<string>();
            while (query[position] != 0)
            {
                labels.Add(Encoding.ASCII.GetString(query, position + 1, query[position]));
                position += 1 + query[position];
            }

            var name = string.Join('.', labels);
            var type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(position + 1));
            var questionEnd = position + 5;
            Queries.Add($"{type} {name}");

            var answers = new List<byte[]>();
            switch (type)
            {
                case 33 when Srv.TryGetValue(name, out var records):
                    // The target shares the zone with the query name, so compress it to a pointer into the question.
                    var zoneOffset = 12 + labels.Take(2).Sum(l => l.Length + 1);
                    foreach (var record in records)
                    {
                        var targetLabels = record.Target.Split('.');
                        var header = new byte[6];
                        BinaryPrimitives.WriteUInt16BigEndian(header, record.Priority);
                        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), record.Weight);
                        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), record.Port);
                        var rdata = new List<byte>(header);
                        if (record.Target.EndsWith(string.Join('.', labels.Skip(2)), StringComparison.OrdinalIgnoreCase))
                        {
                            var prefix = targetLabels.Take(targetLabels.Length - (labels.Count - 2));
                            foreach (var label in prefix)
                            {
                                rdata.Add((byte)label.Length);
                                rdata.AddRange(Encoding.ASCII.GetBytes(label));
                            }

                            rdata.AddRange([(byte)(0xC0 | (zoneOffset >> 8)), (byte)zoneOffset]);
                        }
                        else
                            rdata.AddRange(EncodeName(record.Target));

                        answers.Add(Record(33, [.. rdata]));
                    }

                    break;
                case 12 when Ptr.TryGetValue(name, out var target):
                    answers.Add(Record(12, EncodeName(target)));
                    break;
                case 1 when A.TryGetValue(name, out var address):
                    answers.Add(Record(1, address.GetAddressBytes()));
                    break;
            }

            var response = new List<byte>(query[..2]);
            response.AddRange([0x81, (byte)(answers.Count > 0 ? 0x80 : 0x83), 0, 1, (byte)(answers.Count >> 8), (byte)answers.Count, 0, 0, 0, 0]);
            response.AddRange(query[12..questionEnd]);
            foreach (var answer in answers)
                response.AddRange(answer);

            await _udp.SendAsync(response.ToArray(), received.RemoteEndPoint, _stop.Token);
        }
    }

    private static byte[] Record(ushort type, byte[] data) =>
    [
        0xC0, 0x0C, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 60, (byte)(data.Length >> 8), (byte)data.Length, .. data,
    ];

    private static byte[] EncodeName(string name) =>
    [
        .. name.Split('.').SelectMany(label => new[] { (byte)label.Length }.Concat(Encoding.ASCII.GetBytes(label))),
        0,
    ];
}
