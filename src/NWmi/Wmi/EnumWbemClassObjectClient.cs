using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Wmio;

namespace NWmi.Wmi;

/// <summary>Timeouts for <see cref="EnumWbemClassObjectClient.NextAsync"/>/<c>Skip</c> (WBEM_TIMEOUT_TYPE, MS-WMI Appendix A).</summary>
internal static class WbemTimeout
{
    public const int NoWait = 0;
    public const int Infinite = -1; // 0xFFFFFFFF as the unsigned wire value
}

/// <summary>What <see cref="EnumWbemClassObjectClient.NextAsync"/> returns (MS-WMI 3.1.4.4.2).</summary>
/// <param name="Objects">The objects returned; an entry is null only if the server sent a null interface pointer for it.</param>
/// <param name="HasMore">False (status WBEM_S_FALSE) when this was the end of the enumeration or fewer objects than asked for came back.</param>
internal sealed record WbemNextResult(IReadOnlyList<WmioObjectBlock?> Objects, bool HasMore);

/// <summary>
/// IEnumWbemClassObject (MS-WMI 3.1.4.4): enumerates the CIM objects a query, <c>CreateInstanceEnum</c>, or
/// <c>CreateClassEnum</c> produced. Obtained from <see cref="WbemServicesClient"/>.
/// </summary>
internal sealed class EnumWbemClassObjectClient : IAsyncDisposable
{
    private const ushort OpReset = 3;
    private const ushort OpNext = 4;
    private const ushort OpClone = 6;
    private const ushort OpSkip = 7;

    private readonly WmiOrpcConnection _connection;

    internal EnumWbemClassObjectClient(WmiOrpcConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Reset (opnum 3): rewinds the enumeration, if the server supports it (it need not, e.g. with WBEM_FLAG_FORWARD_ONLY).</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallAsync(OpReset, _ => { }, cancellationToken).ConfigureAwait(false);
        WmiOrpcConnection.ReadStatus(reader, "Reset");
    }

    /// <summary>Next (opnum 4): fetches up to <paramref name="count"/> objects, waiting at most <paramref name="timeoutMilliseconds"/> (<see cref="WbemTimeout"/>).</summary>
    public async Task<WbemNextResult> NextAsync(uint count, int timeoutMilliseconds = WbemTimeout.Infinite,
        CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallAsync(OpNext, writer =>
        {
            writer.WriteInt32(timeoutMilliseconds);
            writer.WriteUInt32(count);
        }, cancellationToken).ConfigureAwait(false);

        // apObjects has no pointer attribute of its own, so (like every other top-level out parameter in this
        // codebase) it defaults to [ref] and so carries no referent id of its own:
        // just the conformant-varying array header, directly.
        var maxCount = reader.ReadMaxCount();
        var offset = reader.ReadUInt32();
        var actualCount = reader.ReadMaxCount();
        if (offset != 0 || actualCount > maxCount)
            throw new NdrFormatException(
                $"Next: array bounds are inconsistent (max {maxCount}, offset {offset}, actual {actualCount}).");

        reader.EnsureCount(actualCount, 4);

        // Each element is itself a [unique] MInterfacePointer*: its referent ids are inline in the array, and
        // (C706 14.3.12) all their pointees are deferred together until this array's own structure ends.
        var pointers = reader.Struct(r =>
        {
            var items = new List<NdrDeferred<byte[]?>>(actualCount);
            for (var i = 0; i < actualCount; i++)
                items.Add(r.ReadMInterfacePointer());

            return items;
        });

        var returned = reader.ReadUInt32();
        if (returned != (uint)actualCount)
            throw new NdrFormatException(
                $"Next: puReturned ({returned}) does not match the array's actual_count ({actualCount}).");

        var status = WmiOrpcConnection.ReadStatus(reader, "Next");
        var objects =
            pointers.ConvertAll(p => p.IsNull || p.Value is null ? null : WmiClassObjectCodec.Decode(p.Value));
        return new WbemNextResult(objects, status != (int)WbemStatus.False);
    }

    /// <summary>Clone (opnum 6): a new, independent enumerator starting from this one's current position.</summary>
    public async Task<EnumWbemClassObjectClient> CloneAsync(CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallReturningReferencesAsync(OpClone, _ => { }, cancellationToken)
            .ConfigureAwait(false);
        var enumPointer = reader.ReadMInterfacePointer();
        WmiOrpcConnection.ReadStatus(reader, "Clone");
        return await WmiOrpcConnection
                   .ConnectOrBindAsync(_connection, enumPointer.Value, WmiIds.IidEnumWbemClassObject,
                       static connection => new EnumWbemClassObjectClient(connection))
                   .ConfigureAwait(false)
               ?? throw new NdrFormatException("Clone succeeded but returned no enumerator.");
    }

    /// <summary>
    /// The optimized path (MS-WMI 4.2.2.3): QueryInterface for IWbemFetchSmartEnum, then GetSmartEnum. Returns
    /// null when the server does not support it, in which case <see cref="NextAsync"/> is the fallback.
    /// </summary>
    public async Task<WbemWcoSmartEnumClient?> TryGetSmartEnumAsync(CancellationToken cancellationToken = default)
    {
        var fetchConnection = await _connection.QueryInterfaceAsync(WmiIds.IidWbemFetchSmartEnum, cancellationToken)
            .ConfigureAwait(false);
        if (fetchConnection is null)
            return null;

        return await WbemWcoSmartEnumClient.GetSmartEnumAsync(fetchConnection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Skip (opnum 7): advances past <paramref name="count"/> objects without fetching them.</summary>
    public async Task SkipAsync(uint count, int timeoutMilliseconds = WbemTimeout.Infinite,
        CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallAsync(OpSkip, writer =>
        {
            writer.WriteInt32(timeoutMilliseconds);
            writer.WriteUInt32(count);
        }, cancellationToken).ConfigureAwait(false);
        WmiOrpcConnection.ReadStatus(reader, "Skip");
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
