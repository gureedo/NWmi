using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Client;

/// <summary>
/// A server-side refresher (<c>IWbemRefreshingServices</c> and <c>IWbemRemoteRefresher</c>, MS-WMI 3.1.4.12 and
/// 3.1.4.13): register objects and class enumerations once, then re-read all of them with one call. Cheaper than
/// repeating a query for performance classes such as <c>Win32_PerfRawData_PerfOS_Memory</c>. Items the provider
/// cannot refresh itself still work, they are just re-read the ordinary way on each refresh.
/// Create one with <see cref="WmiConnection.CreateRefresherAsync"/>.
/// </summary>
/// <example>
/// <code>
/// await using var refresher = await wmi.CreateRefresherAsync();
/// var memory = await refresher.AddObjectAsync("Win32_PerfRawData_PerfOS_Memory=@");
/// for (var i = 0; i &lt; 10; i++)
/// {
///     await refresher.RefreshAsync();
///     Console.WriteLine(memory.Instance!["AvailableBytes"]);
///     await Task.Delay(1000);
/// }
/// </code>
/// </example>
public sealed class WmiRefresher : IAsyncDisposable
{
    private readonly WmiConnection _connection;
    private readonly WbemRefreshingServicesClient? _services;
    private readonly WbemRefresherId _id = WbemRefresherId.Create();
    private readonly List<WmiRefreshItem> _items = [];
    private WbemRemoteRefresherClient? _remote;
    private int _nextLocalId = -1;

    internal WmiRefresher(WmiConnection connection, WbemRefreshingServicesClient? services)
    {
        _connection = connection;
        _services = services;
    }

    /// <summary>The items currently registered.</summary>
    public IReadOnlyList<WmiRefreshItem> Items => _items;

    /// <summary>Registers one instance, by object path (<c>Win32_PerfRawData_PerfOS_Memory=@</c>, <c>Win32_Process.Handle="4"</c>).</summary>
    public Task<WmiRefreshItem> AddObjectAsync(string objectPath, CancellationToken cancellationToken = default) =>
        AddAsync(objectPath, isEnumeration: false, cancellationToken);

    /// <summary>Registers every instance of a class; <see cref="WmiRefreshItem.Objects"/> then holds the current set.</summary>
    /// <exception cref="ArgumentException"><paramref name="className"/> is not a CIM class name.</exception>
    public Task<WmiRefreshItem> AddEnumerationAsync(string className, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);
        if (!WmiConnection.IsClassName(className))
            throw new ArgumentException($"'{className}' is not a CIM class name.", nameof(className));

        return AddAsync(className, isEnumeration: true, cancellationToken);
    }

    private async Task<WmiRefreshItem> AddAsync(string target, bool isEnumeration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        var serverTarget = isEnumeration ? target : _connection.ToServerPath(target);
        WmiRefreshItem item;
        WbemRefreshInfo? info = null;
        if (_services is not null)
            info = isEnumeration
                ? await _services.AddEnumAsync(_id, serverTarget, null, cancellationToken).ConfigureAwait(false)
                : await _services.AddObjectAsync(_id, serverTarget, null, cancellationToken).ConfigureAwait(false);

        if (info is { Type: WbemRefreshType.Remote, Refresher: not null })
        {
            // The server hands out the same refresher for every registration of this client id; keep one.
            if (_remote is null)
                _remote = info.Refresher;
            else
                await info.Refresher.DisposeAsync().ConfigureAwait(false);

            item = new WmiRefreshItem(target, isEnumeration, true, info.CancelId, info.Template?.Instance?.Class);
        }
        else
            item = new WmiRefreshItem(target, isEnumeration, false, _nextLocalId--, null);

        _items.Add(item);
        return item;
    }

    /// <summary>
    /// Re-reads every registered item: one call for the items the provider refreshes itself, and an ordinary read for each
    /// of the others. Results land in <see cref="WmiRefreshItem.Instance"/> and <see cref="WmiRefreshItem.Objects"/>.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var active = _items.Where(i => !i.Removed).ToList();
        if (_remote is not null && active.Any(i => i.IsHighPerformance))
        {
            var byId = active.Where(i => i.IsHighPerformance).ToDictionary(i => i.CancelId);
            foreach (var refreshed in await _remote.RemoteRefreshAsync(cancellationToken).ConfigureAwait(false))
                if (byId.TryGetValue(refreshed.RequestId, out var item))
                    Apply(item, refreshed);
        }

        foreach (var item in active.Where(i => !i.IsHighPerformance))
            if (item.IsEnumeration)
            {
                var rows = new List<WmiInstance>();
                await foreach (var row in _connection.QueryAsync($"SELECT * FROM {item.Target}",
                                   cancellationToken: cancellationToken).ConfigureAwait(false))
                    rows.Add(row);

                item.Objects = rows;
            }
            else
                item.Instance = await _connection.GetInstanceAsync(item.Target, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
    }

    private void Apply(WmiRefreshItem item, WbemRemoteRefresherClient.WbemRefreshedObject refreshed)
    {
        item.LastRefreshFailed = refreshed.BlobType == WbemInstanceBlobType.Error;
        if (item.LastRefreshFailed || item.Template is null)
            return;

        switch (refreshed.BlobType)
        {
            case WbemInstanceBlobType.All:
                item.Instance = Wrap(RefreshedInstanceDecoder.DecodeSingle(item.Template, refreshed.Blob));
                break;
            case WbemInstanceBlobType.Enum:
                item.Objects = RefreshedInstanceDecoder.DecodeEnum(item.Template, refreshed.Blob).ConvertAll(Wrap);
                break;
            default:
                throw new WmioFormatException($"Unknown refreshed blob type {refreshed.BlobType}.");
        }
    }

    private WmiInstance Wrap(WmioInstance instance) => _connection.Wrap(WmioObjectBlock.ForInstance(instance));

    /// <summary>Stops refreshing <paramref name="item"/>.</summary>
    public async Task RemoveAsync(WmiRefreshItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_items.Remove(item))
            return;

        item.Removed = true;
        if (item.IsHighPerformance && _remote is not null)
            await _remote.StopRefreshingAsync([item.CancelId], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the refresher's high-performance items on the server and releases its remote refresher; errors from a server that is already gone are ignored.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_remote is not null)
        {
            var ids = _items.Where(i => i is { IsHighPerformance: true, Removed: false }).Select(i => i.CancelId)
                .ToList();
            try
            {
                if (ids.Count > 0)
                    await _remote.StopRefreshingAsync(ids).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Disposing must not fail because the server is already gone; it drops the refresher with the session.
            }

            await _remote.DisposeAsync().ConfigureAwait(false);
        }

        if (_services is not null)
            await _services.DisposeAsync().ConfigureAwait(false);
    }
}
