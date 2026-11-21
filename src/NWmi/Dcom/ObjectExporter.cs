using System.Buffers.Binary;
using NWmi.Ndr;
using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>What <c>ComplexPing</c> returns.</summary>
/// <param name="SetId">The (possibly new) SETID; non-zero on success.</param>
/// <param name="BackoffFactor">A hint to reduce ping frequency; clients may ignore it.</param>
internal sealed record ComplexPingResult(ulong SetId, ushort BackoffFactor);

/// <summary>What <c>ServerAlive2</c> returns: the resolver's DCOM version and where it can be reached.</summary>
internal sealed record ServerAlive2Result(Version Version, DualStringArray Bindings);

/// <summary>What <c>ResolveOxid2</c> returns for an object exporter.</summary>
/// <param name="Bindings">String bindings with endpoints, e.g. "10.0.0.5[49668]", and security bindings.</param>
/// <param name="RemUnknownIpid">IPID of the exporter's IRemUnknown2, the object that manages references.</param>
/// <param name="AuthnHint">Suggested authentication level.</param>
/// <param name="Version">The exporter's DCOM version.</param>
internal sealed record ResolveOxidResult(
    DualStringArray Bindings,
    Guid RemUnknownIpid,
    RpcAuthLevel AuthnHint,
    Version Version);

/// <summary>
/// The object resolver's IObjectExporter interface (MS-DCOM 3.1.2.5.1), which every machine serves on its RPC
/// endpoint mapper port. It is a plain RPC interface: its calls carry no ORPCTHIS.
/// </summary>
internal sealed class ObjectExporter
{
    public static readonly RpcSyntaxId Interface = RpcSyntaxId.Parse("99fcfec4-5260-101b-bbcb-00aa0021347a");

    private const ushort OpSimplePing = 1;
    private const ushort OpComplexPing = 2;
    private const ushort OpResolveOxid2 = 4;
    private const ushort OpServerAlive2 = 5;

    private readonly RpcClient _client;
    private readonly RpcPresentationContext _context;

    /// <param name="client">A connection bound to <see cref="Interface"/>.</param>
    /// <param name="context">The presentation context that bind returned for it.</param>
    public ObjectExporter(RpcClient client, RpcPresentationContext context)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(context);
        _client = client;
        _context = context;
    }

    /// <summary>Asks the resolver for its version and bindings. It needs no permissions, so it works unauthenticated.</summary>
    public async Task<ServerAlive2Result> ServerAlive2Async(CancellationToken cancellationToken = default)
    {
        var reply = await _client
            .CallAsync(_context, OpServerAlive2, ReadOnlyMemory<byte>.Empty, null, cancellationToken)
            .ConfigureAwait(false);
        return ParseServerAlive2Reply(reply);
    }

    /// <summary>
    /// Pings a ping set previously created with <see cref="ComplexPingAsync"/>, resetting its expiry timer so the
    /// objects it refers to are not garbage-collected (MS-DCOM 3.1.2.5.1.2).
    /// </summary>
    public async Task SimplePingAsync(ulong setId, CancellationToken cancellationToken = default)
    {
        var writer = new NdrWriter();
        writer.WriteUInt64(setId);
        var reply = await _client
            .CallAsync(_context, OpSimplePing, writer.ToArray(), null, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfFailed(reply);
    }

    /// <summary>
    /// Creates a ping set (when <paramref name="setId"/> is 0) or changes the membership of an existing one
    /// (MS-DCOM 3.1.2.5.1.3). <paramref name="addToSet"/> and <paramref name="delFromSet"/> are OIDs
    /// (<see cref="StdObjRef.Oid"/>); pass an empty list for either one not needed.
    /// </summary>
    public async Task<ComplexPingResult> ComplexPingAsync(
        ulong setId,
        ushort sequenceNumber,
        IReadOnlyList<ulong> addToSet,
        IReadOnlyList<ulong> delFromSet,
        CancellationToken cancellationToken = default)
    {
        var reply = await _client.CallAsync(
                _context, OpComplexPing, BuildComplexPingRequest(setId, sequenceNumber, addToSet, delFromSet), null,
                cancellationToken)
            .ConfigureAwait(false);
        return ParseComplexPingReply(reply);
    }

    /// <summary>pSetId, SequenceNum, the two counts, then each OID list as a unique pointer to a conformant array.</summary>
    internal static byte[] BuildComplexPingRequest(ulong setId, ushort sequenceNumber, IReadOnlyList<ulong> addToSet,
        IReadOnlyList<ulong> delFromSet)
    {
        ArgumentNullException.ThrowIfNull(addToSet);
        ArgumentNullException.ThrowIfNull(delFromSet);
        var writer = new NdrWriter();
        writer.WriteUInt64(setId);
        writer.WriteUInt16(sequenceNumber);
        writer.WriteUInt16((ushort)addToSet.Count);
        writer.WriteUInt16((ushort)delFromSet.Count);
        WriteOidArray(writer, addToSet);
        WriteOidArray(writer, delFromSet);
        return writer.ToArray();
    }

    private static void WriteOidArray(NdrWriter writer, IReadOnlyList<ulong> oids)
    {
        writer.WriteUniquePointer(oids.Count > 0, oids.Count == 0
            ? null
            : pw =>
            {
                pw.WriteMaxCount(oids.Count);
                foreach (var oid in oids)
                    pw.WriteUInt64(oid);
            });
    }

    /// <summary>pSetId (the possibly new or unchanged SETID), pPingBackoffFactor, then the status.</summary>
    internal static ComplexPingResult ParseComplexPingReply(byte[] stub)
    {
        ThrowIfFailed(stub);
        var reader = new NdrReader(stub);
        var newSetId = reader.ReadUInt64();
        var backoff = reader.ReadUInt16();
        return new ComplexPingResult(newSetId, backoff);
    }

    /// <summary>Looks up the bindings of the object exporter with the given OXID, for the given protocol sequences.</summary>
    public async Task<ResolveOxidResult> ResolveOxid2Async(
        ulong oxid, IReadOnlyList<ushort> protocolSequences, CancellationToken cancellationToken = default)
    {
        var reply = await _client.CallAsync(
                _context, OpResolveOxid2, BuildResolveOxid2Request(oxid, protocolSequences), null, cancellationToken)
            .ConfigureAwait(false);
        return ParseResolveOxid2Reply(reply);
    }

    /// <summary>OXID, number of protocol sequences, and the sequences as a conformant array (MS-DCOM 3.1.2.5.1.5).</summary>
    internal static byte[] BuildResolveOxid2Request(ulong oxid, IReadOnlyList<ushort> protocolSequences)
    {
        ArgumentNullException.ThrowIfNull(protocolSequences);
        var writer = new NdrWriter();
        writer.WriteUInt64(oxid);
        writer.WriteUInt16(checked((ushort)protocolSequences.Count));
        writer.WriteMaxCount(protocolSequences.Count);
        foreach (var sequence in protocolSequences)
            writer.WriteUInt16(sequence);

        return writer.ToArray();
    }

    /// <summary>COMVERSION, a pointer to the bindings, a reserved DWORD, and the status.</summary>
    internal static ServerAlive2Result ParseServerAlive2Reply(byte[] stub)
    {
        ThrowIfFailed(stub);
        var reader = new NdrReader(stub);
        var version = new Version(reader.ReadUInt16(), reader.ReadUInt16());
        var bindings = ReadBindings(reader);
        reader.ReadUInt32(); // pReserved
        return new ServerAlive2Result(version, bindings);
    }

    /// <summary>A pointer to the bindings, the IPID of IRemUnknown2, the authentication hint, COMVERSION, and the status.</summary>
    internal static ResolveOxidResult ParseResolveOxid2Reply(byte[] stub)
    {
        ThrowIfFailed(stub);
        var reader = new NdrReader(stub);
        var bindings = ReadBindings(reader);
        var ipid = reader.ReadGuid();
        var hint = reader.ReadUInt32();
        var version = new Version(reader.ReadUInt16(), reader.ReadUInt16());
        return new ResolveOxidResult(bindings, ipid, RpcAuthLevels.FromDword(hint), version);
    }

    /// <summary>
    /// The status is the last four bytes of the reply. It is checked first, because a failing method may leave
    /// the other results empty.
    /// </summary>
    private static void ThrowIfFailed(byte[] stub)
    {
        if (stub.Length < 4)
            throw new NdrFormatException("The reply is too short to hold a status.");

        var status = BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(stub.Length - 4));
        if (status != 0)
            throw new DcomException(unchecked((int)status), "The object resolver returned an error");
    }

    /// <summary>A unique pointer to a DUALSTRINGARRAY in its NDR form: wNumEntries, wSecurityOffset, then the entries.</summary>
    private static DualStringArray ReadBindings(NdrReader reader)
    {
        var pointer = reader.ReadUniquePointer(DualStringArray.ReadNdr);
        if (pointer.IsNull)
            throw new NdrFormatException("The object resolver returned no bindings.");

        return pointer.Value;
    }
}
