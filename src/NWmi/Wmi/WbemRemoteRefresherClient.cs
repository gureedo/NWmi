using NWmi.Ndr;

namespace NWmi.Wmi;

/// <summary>
/// IWbemRemoteRefresher (MS-WMI 3.1.4.13): reads the current values of everything registered with the
/// refresher in one call.
/// </summary>
internal sealed class WbemRemoteRefresherClient : IAsyncDisposable
{
    private const ushort OpRemoteRefresh = 3;
    private const ushort OpStopRefreshing = 4;

    private readonly WmiOrpcConnection _connection;

    internal WbemRemoteRefresherClient(WmiOrpcConnection connection)
    {
        _connection = connection;
    }

    /// <summary>One WBEM_REFRESHED_OBJECT (MS-WMI 2.2.15): the blob type tells how to read <paramref name="Blob"/>.</summary>
    internal sealed record WbemRefreshedObject(int RequestId, WbemInstanceBlobType BlobType, byte[] Blob);

    /// <summary>RemoteRefresh (opnum 3).</summary>
    public async Task<IReadOnlyList<WbemRefreshedObject>> RemoteRefreshAsync(
        CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallAsync(OpRemoteRefresh, writer => writer.WriteInt32(0), cancellationToken)
            .ConfigureAwait(false);
        var count = reader.ReadInt32();

        // paObjects is [out, size_is(,*plNumObjects)] WBEM_REFRESHED_OBJECT**: the outer pointer is the implicit
        // [ref] top-level one, the inner one is a unique pointer with its own referent id.
        var referent = reader.ReadUInt32();
        var objects = new List<WbemRefreshedObject>();
        if (referent != 0)
        {
            var max = reader.ReadMaxCount();
            if (max != count)
                throw new NdrFormatException($"RemoteRefresh: {count} objects announced but the array holds {max}.");

            reader.EnsureCount(max, 16);
            var blobs = reader.Struct(r =>
            {
                var items = new List<(int Id, int Type, int Length, NdrDeferred<byte[]> Blob)>(max);
                for (var i = 0; i < max; i++)
                {
                    var id = r.ReadInt32();
                    var type = r.ReadInt32();
                    var length = r.ReadInt32();
                    items.Add((id, type, length, r.ReadUniquePointer(static p => p.ReadConformantBytes())));
                }

                return items;
            });

            foreach (var (id, type, length, blob) in blobs)
            {
                var bytes = blob.IsNull ? [] : blob.Value;
                if (bytes.Length != length)
                    throw new NdrFormatException(
                        $"RemoteRefresh: blob {id} announces {length} bytes but holds {bytes.Length}.");

                objects.Add(new WbemRefreshedObject(id, (WbemInstanceBlobType)type, bytes));
            }
        }

        WmiOrpcConnection.ReadStatus(reader, "RemoteRefresh");
        return objects;
    }

    /// <summary>StopRefreshing (opnum 4): stops refreshing the registrations with these cancel ids.</summary>
    public async Task StopRefreshingAsync(IReadOnlyList<int> cancelIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cancelIds);
        var reader = await _connection.CallAsync(OpStopRefreshing, writer =>
        {
            writer.WriteInt32(cancelIds.Count);
            writer.WriteMaxCount(cancelIds.Count);
            foreach (var id in cancelIds)
                writer.WriteInt32(id);

            writer.WriteInt32(0);
        }, cancellationToken).ConfigureAwait(false);
        WmiOrpcConnection.ReadStatus(reader, "StopRefreshing");
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
