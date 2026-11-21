using NWmi.Ndr;
using NWmi.Rpc;

namespace NWmi.Dcom.ActivationProperty;

/// <summary>
/// ScmRequestInfoData (MS-DCOM 2.2.22.2.4), wrapping custom REMOTE_REQUEST_SCM_INFO: the RPC protocol sequences
/// the client can use, so the server can answer with bindings the client is able to reach.
/// </summary>
internal sealed class ScmRequestInfoData : IActivationProperty
{
    public static readonly Guid PropertyClsid = Guid.Parse("000001aa-0000-0000-c000-000000000046");

    /// <inheritdoc />
    public Guid Clsid => PropertyClsid;

    /// <summary>
    /// The ClientImpLevel this client sends. The spec only asks for an implementation-specific value that the server
    /// ignores (MS-DCOM 2.2.22.2.4.1); Windows DCOM clients send 2 (product behavior note 33), which is
    /// <see cref="RpcImpersonationLevel.Identify"/> in rpcdce.h's numbering, so this does the same.
    /// </summary>
    private const RpcImpersonationLevel ClientImpersonationLevel = RpcImpersonationLevel.Identify;

    /// <summary>Upper bound on the number of protocol sequences (MAX_REQUESTED_PROTSEQS, MS-DCOM 2.2.28.1).</summary>
    public const int MaxRequestedProtocolSequences = 0x8000;

    public ScmRequestInfoData(IReadOnlyList<ushort> requestedProtocolSequences)
    {
        ArgumentNullException.ThrowIfNull(requestedProtocolSequences);
        if (requestedProtocolSequences.Count > MaxRequestedProtocolSequences)
            throw new ArgumentOutOfRangeException(nameof(requestedProtocolSequences));

        RequestedProtocolSequences = requestedProtocolSequences;
    }

    /// <summary>Protocol sequence identifiers, for example <see cref="DualStringArray.TcpTowerId"/>.</summary>
    public IReadOnlyList<ushort> RequestedProtocolSequences { get; }

    /// <summary>The NDR encoding, without the type serialization framing.</summary>
    public byte[] Encode()
    {
        var writer = new NdrWriter();
        writer.Struct(w =>
        {
            w.WriteUniquePointer(false, null); // pdwReserved
            w.WriteUniquePointer(true, rw =>
            {
                rw.WriteUInt32((uint)ClientImpersonationLevel); // ClientImpLevel
                rw.WriteUInt16((ushort)RequestedProtocolSequences.Count);
                rw.WriteUniquePointer(true, pw =>
                {
                    pw.WriteMaxCount(RequestedProtocolSequences.Count);
                    foreach (var sequence in RequestedProtocolSequences)
                        pw.WriteUInt16(sequence);
                });
            });
        });

        return writer.ToArray();
    }

    public static ScmRequestInfoData Decode(ReadOnlySpan<byte> data)
    {
        var reader = new NdrReader(data.ToArray());
        NdrDeferred<List<ushort>>? sequences = null;
        reader.Struct(r =>
        {
            r.ReadUniquePointer(pr => pr.ReadUInt32()); // pdwReserved
            var request = r.ReadUniquePointer(rr =>
            {
                rr.ReadUInt32(); // ClientImpLevel: ignored
                var count = rr.ReadUInt16();
                sequences = rr.ReadUniquePointer(pr => ReadSequences(pr, count));
                return 0;
            });

            if (request.IsNull)
                throw new NdrFormatException("ScmRequestInfoData has no request structure.");

            return 0;
        });

        if (sequences is null || sequences.IsNull)
            return new ScmRequestInfoData([]);

        return new ScmRequestInfoData(sequences.Value);
    }

    private static List<ushort> ReadSequences(NdrReader reader, ushort count)
    {
        var actual = reader.ReadMaxCount();
        if (actual != count)
            throw new NdrFormatException($"The protocol sequence array has {actual} entries but cRequestedProtseqs says {count}.");

        reader.EnsureCount(count, 2);
        var result = new List<ushort>(count);
        for (var i = 0; i < count; i++)
            result.Add(reader.ReadUInt16());

        return result;
    }
}
