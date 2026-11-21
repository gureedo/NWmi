using System.Buffers.Binary;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>
/// The object exporter's IRemUnknown2 (MS-DCOM 3.1.1.5.6, 3.1.1.5.7): manages the reference counts of the interfaces
/// it exports. Calls go to the exporter's own endpoint, with the IPID from <see cref="ScmReplyInfoData.RemUnknownIpid"/>
/// as the object UUID.
/// </summary>
internal sealed class RemUnknown2
{
    private const ushort OpRemQueryInterface2 = 6;
    private const ushort OpRemAddRef = 4;
    private const ushort OpRemRelease = 5;
    private const uint ENoInterface = 0x80004002;
    private const uint SeverityBit = 0x80000000;

    private readonly RpcClient _client;
    private readonly RpcPresentationContext _context;
    private readonly Guid _remUnknownIpid;

    /// <param name="client">A connection to the exporter, bound to IRemUnknown2 (or IRemUnknown).</param>
    /// <param name="context">The presentation context that bind returned.</param>
    /// <param name="remUnknownIpid">The exporter's IRemUnknown IPID, from activation or ResolveOxid2.</param>
    public RemUnknown2(RpcClient client, RpcPresentationContext context, Guid remUnknownIpid)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(context);
        _client = client;
        _context = context;
        _remUnknownIpid = remUnknownIpid;
    }

    /// <summary>
    /// Asks the interface identified by <paramref name="ipid"/> for pointers to the given <paramref name="iids"/>.
    /// Each entry of the result is independent: one interface can fail while others succeed.
    /// </summary>
    public async Task<IReadOnlyList<InterfaceResult>> QueryInterfaceAsync(
        Guid ipid, IReadOnlyList<Guid> iids, CancellationToken cancellationToken = default)
    {
        var request = BuildQueryInterfaceRequest(Guid.NewGuid(), ipid, iids);
        var reply = await _client.CallAsync(_context, OpRemQueryInterface2, request, _remUnknownIpid, cancellationToken)
            .ConfigureAwait(false);
        return ParseQueryInterfaceReply(reply, iids);
    }

    /// <summary>
    /// ORPCTHIS, the target IPID, then the IIDs as a conformant array. RemQueryInterface2's IDL (MS-DCOM
    /// 3.1.1.5.7.1.1) has no reference-count-style parameter at all (unlike the older RemQueryInterface,
    /// which takes a <c>cRefs</c>): a server rejects a request carrying one with RPC_X_BAD_STUB_DATA,
    /// since it shifts every field after it by 4 bytes.
    /// </summary>
    internal static byte[] BuildQueryInterfaceRequest(Guid causalityId, Guid ipid, IReadOnlyList<Guid> iids)
    {
        ArgumentNullException.ThrowIfNull(iids);
        if (iids.Count is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(iids), iids.Count, "Between 1 and 65535 IIDs are allowed.");

        var writer = new NdrWriter();
        OrpcHeader.WriteThis(writer, causalityId);
        writer.WriteGuid(ipid);
        writer.WriteUInt16((ushort)iids.Count);
        writer.WriteMaxCount(iids.Count);
        foreach (var iid in iids)
            writer.WriteGuid(iid);

        return writer.ToArray();
    }

    /// <summary>
    /// ORPCTHAT, the per-IID HRESULTs, the per-IID MInterfacePointers, then the method's own HRESULT
    /// (RemQueryInterface2 returns HRESULT* and PMInterfacePointerInternal* instead of a REMQIRESULT array).
    /// </summary>
    internal static List<InterfaceResult> ParseQueryInterfaceReply(byte[] stub, IReadOnlyList<Guid> iids)
    {
        if (stub.Length < 4)
            throw new NdrFormatException("The reply is too short to hold a status.");

        // E_NOINTERFACE for the whole call is the same answer as each interface failing, and the arrays may not be there.
        var status = BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(stub.Length - 4));
        if (status == ENoInterface)
            return [.. iids.Select(iid => new InterfaceResult(iid, unchecked((int)ENoInterface), null))];

        // S_FALSE and other success codes mean a partial result, which the per-interface HRESULTs spell out.
        if ((status & SeverityBit) != 0)
            throw new DcomException(unchecked((int)status), "RemQueryInterface2 failed");

        var reader = new NdrReader(stub);
        OrpcHeader.ReadThat(reader);

        var resultCount = reader.ReadMaxCount();
        if (resultCount != iids.Count)
            throw new NdrFormatException($"The reply holds {resultCount} HRESULTs for {iids.Count} requested IIDs.");

        reader.EnsureCount(resultCount, 4);
        var hresults = new int[resultCount];
        for (var i = 0; i < resultCount; i++)
            hresults[i] = reader.ReadInt32();

        var pointerCount = reader.ReadMaxCount();
        if (pointerCount != iids.Count)
            throw new NdrFormatException(
                $"The reply holds {pointerCount} interface pointers for {iids.Count} requested IIDs.");

        reader.EnsureCount(pointerCount, 4);
        var pointers = new List<NdrDeferred<byte[]?>>(pointerCount);
        for (var i = 0; i < pointerCount; i++)
            pointers.Add(reader.ReadMInterfacePointer());

        reader.ReadUInt32(); // the method's HRESULT, already checked above
        if (!reader.IsAtEnd)
            throw new NdrFormatException("Unexpected data after the results.");

        var results = new List<InterfaceResult>(iids.Count);
        for (var i = 0; i < iids.Count; i++)
        {
            var objRef = pointers[i].Value;
            if (hresults[i] >= 0 && objRef is null)
                throw new NdrFormatException($"Interface {iids[i]} succeeded but no object reference was returned.");

            results.Add(new InterfaceResult(iids[i], hresults[i], objRef));
        }

        return results;
    }

    /// <summary>Takes additional references on interfaces. Returns one HRESULT per entry (negative means that entry failed).</summary>
    public async Task<IReadOnlyList<int>> AddRefAsync(IReadOnlyList<RemInterfaceRef> references,
        CancellationToken cancellationToken = default)
    {
        var request = BuildReferenceRequest(Guid.NewGuid(), references);
        var reply = await _client.CallAsync(_context, OpRemAddRef, request, _remUnknownIpid, cancellationToken)
            .ConfigureAwait(false);
        return ParseAddRefReply(reply, references.Count);
    }

    /// <summary>Gives back references the client holds, so the server can free the objects when none remain.</summary>
    public async Task ReleaseAsync(IReadOnlyList<RemInterfaceRef> references,
        CancellationToken cancellationToken = default)
    {
        var request = BuildReferenceRequest(Guid.NewGuid(), references);
        var reply = await _client.CallAsync(_context, OpRemRelease, request, _remUnknownIpid, cancellationToken)
            .ConfigureAwait(false);
        ParseStatusOnlyReply(reply);
    }

    /// <summary>ORPCTHIS, the number of entries, and the entries as a conformant array (RemAddRef and RemRelease share this part).</summary>
    internal static byte[] BuildReferenceRequest(Guid causalityId, IReadOnlyList<RemInterfaceRef> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(references), references.Count,
                "Between 1 and 65535 entries are allowed.");

        var writer = new NdrWriter();
        OrpcHeader.WriteThis(writer, causalityId);
        writer.WriteUInt16((ushort)references.Count);
        writer.WriteMaxCount(references.Count);
        foreach (var reference in references)
            reference.WriteNdr(writer);

        return writer.ToArray();
    }

    /// <summary>ORPCTHAT, the per-entry HRESULTs as a conformant array, then the method's own HRESULT.</summary>
    internal static List<int> ParseAddRefReply(byte[] stub, int expectedCount)
    {
        if (stub.Length < 4)
            throw new NdrFormatException("The reply is too short to hold a status.");

        var status = BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(stub.Length - 4));
        if (status != 0)
            throw new DcomException(unchecked((int)status), "The remote reference operation failed");

        var reader = new NdrReader(stub);
        OrpcHeader.ReadThat(reader);
        var count = reader.ReadMaxCount();
        if (count != expectedCount)
            throw new NdrFormatException($"The reply holds {count} results for {expectedCount} entries.");

        reader.EnsureCount(count, 4);
        var results = new List<int>(count);
        for (var i = 0; i < count; i++)
            results.Add(reader.ReadInt32());

        reader.ReadUInt32(); // the method's HRESULT, already checked above
        if (!reader.IsAtEnd)
            throw new NdrFormatException("Unexpected data after the results.");

        return results;
    }

    /// <summary>ORPCTHAT followed by the HRESULT of a method with no other results.</summary>
    internal static void ParseStatusOnlyReply(byte[] stub)
    {
        if (stub.Length < 4)
            throw new NdrFormatException("The reply is too short to hold a status.");

        var status = BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(stub.Length - 4));
        var reader = new NdrReader(stub);
        OrpcHeader.ReadThat(reader);
        if (reader.Remaining != 4)
            throw new NdrFormatException($"Expected only a status after ORPCTHAT but {reader.Remaining} bytes follow.");

        if (status != 0)
            throw new DcomException(unchecked((int)status), "The remote reference operation failed");
    }
}
