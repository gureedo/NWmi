using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Wmio;

namespace NWmi.Wmi;

/// <summary>
/// IWbemWCOSmartEnum (MS-WMI 3.1.4.7): the optimized enumerator, whose <see cref="NextAsync"/> returns a whole
/// batch of objects in one ObjectArray buffer (MS-WMI 2.2.14) instead of one marshaled interface pointer each.
/// <c>proxyGUID</c> identifies this enumeration session to the server across every <see cref="NextAsync"/>
/// call, so that the server need only ever send one class's definition once for the session's whole lifetime
/// (MS-WMI 2.2.14's "maintained by both the server and the client for the duration of the method call");
/// this client's own <see cref="_classCache"/> mirrors that and likewise lives for the client's lifetime, not
/// just one call. It caches each class's raw, undecoded CurrentClass bytes (not a decoded
/// <see cref="WmioClass"/>): a later classless instance's own data is wire bytes built against the
/// <em>original</em> encoder's ValueTable packing, which follows property declaration order, not the
/// alphabetically-sorted order <see cref="WmioClass.Properties"/> exposes it in; re-encoding a decoded
/// <see cref="WmioClass"/> to splice it back in reassigns every property a fresh offset in that sorted
/// order, silently disagreeing with the untouched instance bytes that follow it (seen with a multipage
/// <c>Win32_NTLogEvent</c> enumeration). Caching the original bytes verbatim sidesteps this
/// entirely.
/// </summary>
internal sealed class WbemWcoSmartEnumClient : IAsyncDisposable
{
    private const ushort OpNext = 3;
    private const ushort OpGetSmartEnum = 3;

    private readonly WmiOrpcConnection _connection;
    private readonly Guid _proxyGuid = Guid.NewGuid();
    private readonly Dictionary<Guid, byte[]> _classCache = [];

    internal WbemWcoSmartEnumClient(WmiOrpcConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// IWbemFetchSmartEnum::GetSmartEnum (MS-WMI 3.1.4.6, opnum 3) on <paramref name="fetchConnection"/>, a connection
    /// to that interface (see <see cref="EnumWbemClassObjectClient.TryGetSmartEnumAsync"/>). The fetch connection is
    /// disposed once the enumerator is bound.
    /// </summary>
    public static async Task<WbemWcoSmartEnumClient> GetSmartEnumAsync(WmiOrpcConnection fetchConnection,
        CancellationToken cancellationToken = default)
    {
        await using var _ = fetchConnection.ConfigureAwait(false);
        var reader = await fetchConnection.CallReturningReferencesAsync(OpGetSmartEnum, _ => { }, cancellationToken)
            .ConfigureAwait(false);
        var pointer = reader.ReadMInterfacePointer();
        WmiOrpcConnection.ReadStatus(reader, "GetSmartEnum");
        return await WmiOrpcConnection
                   .ConnectOrBindAsync(fetchConnection, pointer.Value, WmiIds.IidWbemWCOSmartEnum,
                       static connection => new WbemWcoSmartEnumClient(connection))
                   .ConfigureAwait(false)
               ?? throw new NdrFormatException("GetSmartEnum succeeded but returned no enumerator.");
    }

    /// <summary>Next (opnum 3): fetches up to <paramref name="count"/> objects as one ObjectArray buffer.</summary>
    public async Task<WbemNextResult> NextAsync(uint count, int timeoutMilliseconds = WbemTimeout.Infinite,
        CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallAsync(OpNext, writer =>
        {
            writer.WriteGuid(_proxyGuid);
            writer.WriteInt32(timeoutMilliseconds);
            writer.WriteUInt32(count);
        }, cancellationToken).ConfigureAwait(false);

        var returned = reader.ReadUInt32();
        var buffSize = reader.ReadUInt32();

        // pBuffer is declared [out, size_is(,*pdwBuffSize)] byte** (MS-WMI 3.1.4.7.1's IDL): the outer
        // pointer (pBuffer itself) is the implicit top-level [ref] parameter and carries no referent id, but
        // the byte array is one level deeper, behind an inner pointer (*pBuffer) that IS marshaled with its
        // own referent id (NDR's default [unique] for a non-top-level pointer) ahead of the conformant
        // array's own MaxCount; without it the response reads as if the
        // array's declared bound were some arbitrary large value unrelated to pdwBuffSize.
        var innerReferent = reader.ReadUInt32();
        if (innerReferent == 0)
        {
            // No results at all (e.g. a query matching nothing): a null pointer, so no array follows.
            if (returned != 0 || buffSize != 0)
                throw new NdrFormatException(
                    $"Next: pBuffer is null but puReturned is {returned} and pdwBuffSize is {buffSize}.");

            var emptyStatus = WmiOrpcConnection.ReadStatus(reader, "Next");
            return new WbemNextResult([], emptyStatus != (int)WbemStatus.False);
        }

        var bufferMax = reader.ReadMaxCount();
        if (bufferMax != buffSize)
            throw new NdrFormatException(
                $"Next: pBuffer's array bound ({bufferMax}) does not match pdwBuffSize ({buffSize}).");

        reader.EnsureCount(bufferMax, 1);
        var buffer = reader.ReadBytes(bufferMax);
        var status = WmiOrpcConnection.ReadStatus(reader, "Next");

        var objects = ObjectArrayDecoder.Decode(buffer, _classCache);
        if (objects.Count != returned)
            throw new NdrFormatException(
                $"Next: puReturned ({returned}) does not match the ObjectArray's object count ({objects.Count}).");

        return new WbemNextResult(objects, status != (int)WbemStatus.False);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
