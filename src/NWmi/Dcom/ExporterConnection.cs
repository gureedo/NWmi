using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>
/// A connection to an object exporter, used to manage the reference counts of the objects it hosts
/// (MS-DCOM 3.1.1.5.6/3.1.1.5.7). Opened from an <see cref="ActivationResult"/> or a <see cref="ResolveOxidResult"/>:
/// both carry the exporter's bindings and the IPID of its IRemUnknown2.
/// </summary>
internal sealed class ExporterConnection : IAsyncDisposable
{
    private readonly Lock _trackedLock = new();
    private readonly HashSet<TrackedInterfaceReference> _tracked = [];
    private readonly Dictionary<ulong, int> _oidReferences = [];
    private IAsyncDisposable? _pingerOwnedResource;
    private bool _disposed;

    private ExporterConnection(RpcClient rpcClient, RemUnknown2 remUnknown)
    {
        RpcClient = rpcClient;
        RemUnknown = remUnknown;
    }

    /// <summary>The underlying RPC connection, for binding the object's own interfaces alongside IRemUnknown2.</summary>
    public RpcClient RpcClient { get; }

    public RemUnknown2 RemUnknown { get; }

    /// <summary>The keeper that pings the objects tracked on this connection (see <see cref="AttachPinger"/>), or null when none was attached.</summary>
    internal PingKeeper? Pinger { get; private set; }

    /// <summary>
    /// Has <paramref name="pinger"/> keep every object tracked from now on alive (MS-DCOM 3.1.2.2: a client pings the objects it holds, or the server
    /// may collect them). Call before the first <see cref="Track"/>. This connection then owns the keeper: disposing it stops the keeper and
    /// then disposes <paramref name="ownedResource"/>, e.g. the connection to the object resolver that the pings travel on.
    /// </summary>
    internal void AttachPinger(PingKeeper pinger, IAsyncDisposable? ownedResource)
    {
        ArgumentNullException.ThrowIfNull(pinger);
        lock (_trackedLock)
        {
            Pinger = pinger;
            _pingerOwnedResource = ownedResource;
        }
    }

    /// <summary>
    /// Wraps an interface reference so it is released through <see cref="RemUnknown2.ReleaseAsync"/> when
    /// disposed, instead of needing a manual call. Any reference still tracked when this connection itself is
    /// disposed is released then, on a best-effort basis.
    /// </summary>
    public TrackedInterfaceReference Track(StdObjRef std)
    {
        var reference = new TrackedInterfaceReference(this, std);
        lock (_trackedLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _tracked.Add(reference);
            if (Pinger is not null && _oidReferences.GetValueOrDefault(std.Oid) is var held)
            {
                _oidReferences[std.Oid] = held + 1;
                if (held == 0)
                    Pinger.Add(std.Oid);
            }
        }

        return reference;
    }

    internal void Untrack(TrackedInterfaceReference reference)
    {
        lock (_trackedLock)
        {
            _tracked.Remove(reference);
            if (Pinger is null || !_oidReferences.TryGetValue(reference.Oid, out var held))
                return;

            if (held <= 1)
            {
                _oidReferences.Remove(reference.Oid);
                Pinger.Remove(reference.Oid);
            }
            else
                _oidReferences[reference.Oid] = held - 1;
        }
    }

    /// <summary>
    /// Tries each TCP binding <c>BindingSelector.EnumerateExporterTargets</c> offers, in the server's
    /// preference order, and connects to the first one that succeeds (the client cannot always reach the
    /// address a server prefers, e.g. across NAT or on a multi-homed host).
    /// </summary>
    public static Task<ExporterConnection> ConnectAsync(
        DualStringArray bindings, Guid remUnknownIpid, RpcSecurity? security = null,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(bindings, remUnknownIpid, preferredHost: null, security, callOptions: null, cancellationToken);

    public static async Task<ExporterConnection> ConnectAsync(
        DualStringArray bindings, Guid remUnknownIpid, string? preferredHost, RpcSecurity? security,
        RpcClientOptions? callOptions = null, CancellationToken cancellationToken = default)
    {
        var targets = BindingSelector.EnumerateExporterTargets(bindings, preferredHost).ToList();
        if (targets.Count == 0)
            throw new DcomException("No usable TCP binding (with an endpoint) was offered.");

        List<Exception>? failures = null;
        foreach (var target in targets)
            try
            {
                return await ConnectAsync(target.Host, target.Port, remUnknownIpid, security, callOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (failures ??= []).Add(ex);
            }

        throw new DcomException(
            $"Could not connect to any of the {targets.Count} binding(s) offered: " +
            string.Join("; ", targets.Zip(failures!, (t, e) => $"{t.Host}:{t.Port} ({e.Message})")),
            new AggregateException(failures!));
    }

    public static async Task<ExporterConnection> ConnectAsync(
        string host, int port, Guid remUnknownIpid, RpcSecurity? security = null, RpcClientOptions? callOptions = null,
        CancellationToken cancellationToken = default)
    {
        var rpcClient = await RpcClient.ConnectTcpAsync(host, port, callOptions, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await FromRpcClientAsync(rpcClient, remUnknownIpid, security, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await rpcClient.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Binds IRemUnknown2 on an already-connected <see cref="RpcClient"/>; exposed for testing.</summary>
    internal static async Task<ExporterConnection> FromRpcClientAsync(
        RpcClient rpcClient, Guid remUnknownIpid, RpcSecurity? security, CancellationToken cancellationToken)
    {
        var context = (await rpcClient.BindAsync([DcomIds.RemUnknown2], security, cancellationToken)
            .ConfigureAwait(false))[0];
        return new ExporterConnection(rpcClient, new RemUnknown2(rpcClient, context, remUnknownIpid));
    }

    public async ValueTask DisposeAsync()
    {
        TrackedInterfaceReference[] outstanding;
        lock (_trackedLock)
        {
            _disposed = true;
            outstanding = [.. _tracked];
            _tracked.Clear();
        }

        foreach (var reference in outstanding)
            await reference.DisposeAsync().ConfigureAwait(false);

        PingKeeper? pinger;
        IAsyncDisposable? pingerResource;
        lock (_trackedLock)
        {
            pinger = Pinger;
            pingerResource = _pingerOwnedResource;
            Pinger = null;
            _pingerOwnedResource = null;
        }

        if (pinger is not null)
            await pinger.DisposeAsync().ConfigureAwait(false);

        if (pingerResource is not null)
            await pingerResource.DisposeAsync().ConfigureAwait(false);

        await RpcClient.DisposeAsync().ConfigureAwait(false);
    }
}
