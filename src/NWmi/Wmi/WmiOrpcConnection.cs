using NWmi.Dcom;
using NWmi.Dcom.ActivationProperty;
using NWmi.Ndr;
using NWmi.Rpc;

namespace NWmi.Wmi;

/// <summary>
/// A connection to one WMI-protocol interface on a remote object (IWbemLevel1Login, IWbemServices, ...): the
/// exporter connection that hosts it, the presentation context bound to the interface's IID, and the
/// object's IPID (sent as the object UUID of every call, MS-DCOM 3.2.4.2). Wraps the ORPCTHIS/ORPCTHAT framing
/// every WMI ORPC method shares, so each interface-specific client (<see cref="WbemServicesClient"/>, ...) only
/// describes its own parameters.
/// </summary>
internal sealed class WmiOrpcConnection : IAsyncDisposable
{
    private readonly ExporterLease _lease;
    private readonly RpcPresentationContext _context;
    private readonly TrackedInterfaceReference _reference;

    private WmiOrpcConnection(
        ExporterLease lease,
        RpcPresentationContext context,
        TrackedInterfaceReference reference,
        ulong oxid,
        RpcSecurity? security,
        RpcClientOptions? callOptions)
    {
        _lease = lease;
        _context = context;
        _reference = reference;
        Oxid = oxid;
        Security = security;
        CallOptions = callOptions;
    }

    /// <summary>The IPID this connection calls, sent as the object UUID of every request.</summary>
    public Guid Ipid => _reference.Ipid;

    /// <summary>The OXID of the exporter this connection is bound to; objects on the same OXID reuse it (<see cref="ConnectOrBindAsync"/>).</summary>
    public ulong Oxid { get; }

    public RpcClient RpcClient => _lease.Connection.RpcClient;

    public RpcSecurity? Security { get; }

    /// <summary>The RPC client options passed on to connections opened from this one; null for the defaults.</summary>
    public RpcClientOptions? CallOptions { get; }

    /// <summary>The keeper pinging this connection's exporter, or null (see <see cref="ExporterConnection.AttachPinger"/>).</summary>
    internal PingKeeper? Pinger => _lease.Connection.Pinger;

    /// <summary>
    /// Binds <paramref name="interfaceIid"/> on the exporter an activation (MS-DCOM 3.1.2.5.2.3) already resolved, so
    /// no OXID resolution is needed. With <paramref name="pingClient"/>, a connection to the object resolver, the
    /// exporter connection keeps its objects alive by pinging every <paramref name="pingPeriod"/> (default 2 minutes) and takes over
    /// <paramref name="pingClient"/>: it is disposed with the exporter connection, or when this call fails. If that connection is lost,
    /// <paramref name="pingReconnect"/> opens a replacement.
    /// </summary>
    public static Task<WmiOrpcConnection> FromActivationAsync(
        ScmReplyInfoData reply, StdObjRef std, Guid interfaceIid, RpcSecurity? security,
        CancellationToken cancellationToken,
        string? preferredHost = null,
        DcomClient? pingClient = null,
        TimeSpan? pingPeriod = null,
        Func<CancellationToken, Task<DcomClient>>? pingReconnect = null,
        RpcClientOptions? callOptions = null) =>
        ConnectCoreAsync(
            async ct =>
            {
                var exporter = await ExporterConnection
                    .ConnectAsync(reply.Bindings, reply.RemUnknownIpid, preferredHost, security, callOptions, ct)
                    .ConfigureAwait(false);
                if (pingClient is not null)
                    exporter.AttachPinger(new PingKeeper(pingClient.Exporter, pingPeriod, pingReconnect), pingClient);

                return new ExporterLease(exporter, owned: true);
            },
            std, interfaceIid, security, callOptions, cancellationToken);

    /// <summary>
    /// Resolves <paramref name="objRef"/>'s OXID through the object resolver named by its
    /// <see cref="StandardObjRef.ResolverAddress"/> and binds <paramref name="interfaceIid"/> on the exporter
    /// that answer names, trying every TCP binding offered, in order.
    /// </summary>
    public static async Task<WmiOrpcConnection> ConnectAsync(
        StandardObjRef objRef, Guid interfaceIid, RpcSecurity? security = null, RpcClientOptions? callOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objRef);
        var targets = BindingSelector.EnumerateResolverTargets(objRef.ResolverAddress).ToList();
        if (targets.Count == 0)
            throw new DcomException("The object reference carries no usable TCP resolver binding.");

        List<Exception>? failures = null;
        foreach (var target in targets)
            try
            {
                return await ConnectViaResolverAsync(target.Host, target.Port, objRef, interfaceIid, security,
                    callOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (failures ??= []).Add(ex);
            }

        throw new DcomException(
            $"Could not resolve OXID 0x{objRef.Std.Oxid:X} through any of the {targets.Count} resolver binding(s) offered.",
            new AggregateException(failures!));
    }

    /// <summary>Binds <paramref name="interfaceIid"/> on an already-open <see cref="ExporterConnection"/>; exposed for testing against a fake transport.</summary>
    internal static Task<WmiOrpcConnection> FromExporterConnectionAsync(
        ExporterConnection exporterConnection, bool ownsConnection, StdObjRef std, Guid interfaceIid,
        RpcSecurity? security, CancellationToken cancellationToken) =>
        ConnectCoreAsync(_ => Task.FromResult(new ExporterLease(exporterConnection, ownsConnection)), std, interfaceIid,
            security, callOptions: null, cancellationToken);

    /// <summary>
    /// Binds <paramref name="interfaceIid"/> on this same exporter connection. Valid only when the object lives on
    /// this connection's <see cref="Oxid"/>; <see cref="ConnectOrBindAsync"/> picks automatically.
    /// </summary>
    public Task<WmiOrpcConnection> BindSiblingAsync(StdObjRef std, Guid interfaceIid,
        CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(_ => Task.FromResult(_lease.Acquire()), std, interfaceIid, Security, CallOptions,
            cancellationToken);

    /// <summary>
    /// Reuses <paramref name="existing"/>'s exporter connection when <paramref name="objRef"/> shares its OXID,
    /// or opens a fresh one otherwise.
    /// </summary>
    public static Task<WmiOrpcConnection> ConnectOrBindAsync(
        WmiOrpcConnection? existing, StandardObjRef objRef, Guid interfaceIid, RpcSecurity? security,
        CancellationToken cancellationToken) =>
        existing is not null && existing.Oxid == objRef.Std.Oxid
            ? existing.BindSiblingAsync(objRef.Std, interfaceIid, cancellationToken)
            : ConnectAsync(objRef, interfaceIid, security, existing?.CallOptions, cancellationToken);

    /// <summary>
    /// The common shape of an <c>[out] T**</c> parameter that returns another remote object: null when the
    /// pointer was null, otherwise a standard object reference connected (reusing <paramref name="existing"/>
    /// when possible) and wrapped with <paramref name="create"/>.
    /// </summary>
    /// <remarks>Takes no cancellation token: the server has handed out the reference, and stopping here would lose it (see <see cref="CallReturningReferencesAsync"/>).</remarks>
    public static async Task<T?> ConnectOrBindAsync<T>(
        WmiOrpcConnection? existing, byte[]? objRefBytes, Guid interfaceIid, Func<WmiOrpcConnection, T> create)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(create);
        if (objRefBytes is null)
            return null;

        if (ObjRef.Parse(objRefBytes) is not StandardObjRef objRef)
            throw new NdrFormatException("Expected a standard (not custom) object reference.");

        var connection =
            await ConnectOrBindAsync(existing, objRef, interfaceIid, existing?.Security, CancellationToken.None)
                .ConfigureAwait(false);
        return create(connection);
    }

    private static async Task<WmiOrpcConnection> ConnectViaResolverAsync(
        string resolverHost, int resolverPort, StandardObjRef objRef, Guid interfaceIid, RpcSecurity? security,
        RpcClientOptions? callOptions, CancellationToken cancellationToken)
    {
        var resolverRpc = await RpcClient
            .ConnectTcpAsync(resolverHost, resolverPort, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var exporterContext = (await resolverRpc
                .BindAsync([ObjectExporter.Interface], security, cancellationToken).ConfigureAwait(false))[0];
            var resolved = await new ObjectExporter(resolverRpc, exporterContext)
                .ResolveOxid2Async(objRef.Std.Oxid, [DualStringArray.TcpTowerId], cancellationToken)
                .ConfigureAwait(false);
            return await ConnectCoreAsync(
                async ct => new ExporterLease(
                    await ExporterConnection
                        .ConnectAsync(resolved.Bindings, resolved.RemUnknownIpid, resolverHost, security, callOptions,
                            ct)
                        .ConfigureAwait(false),
                    owned: true),
                objRef.Std, interfaceIid, security, callOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await resolverRpc.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<WmiOrpcConnection> ConnectCoreAsync(
        Func<CancellationToken, Task<ExporterLease>> openExporter,
        StdObjRef std,
        Guid interfaceIid,
        RpcSecurity? security,
        RpcClientOptions? callOptions,
        CancellationToken cancellationToken)
    {
        var lease = await openExporter(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = (await lease.Connection.RpcClient
                .AlterContextAsync([WmiIds.AsSyntax(interfaceIid)], cancellationToken).ConfigureAwait(false))[0];
            var reference = lease.Connection.Track(std);
            return new WmiOrpcConnection(lease, context, reference, std.Oxid, security, callOptions);
        }
        catch
        {
            await lease.ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Shares one exporter connection between the objects that live on it (e.g. the IWbemServices that NTLMLogin
    /// returns and the login object). Each holds a lease, and the connection closes when the last is released.
    /// </summary>
    private sealed class ExporterLease(ExporterConnection connection, bool owned)
    {
        private int _holders = 1;

        public ExporterConnection Connection { get; } = connection;

        public ExporterLease Acquire()
        {
            Interlocked.Increment(ref _holders);
            return this;
        }

        public ValueTask ReleaseAsync() =>
            Interlocked.Decrement(ref _holders) == 0 && owned ? Connection.DisposeAsync() : ValueTask.CompletedTask;
    }

    /// <summary>
    /// RemQueryInterface2 for another interface on this same object (MS-DCOM 3.1.1.5.7.1.1), e.g. asking an
    /// IEnumWbemClassObject for IWbemFetchSmartEnum (MS-WMI 4.2.2.3). Returns null if the object does not
    /// support the interface.
    /// </summary>
    public async Task<WmiOrpcConnection?> QueryInterfaceAsync(Guid interfaceIid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = await _lease.Connection.RemUnknown
            .QueryInterfaceAsync(Ipid, [interfaceIid], cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
        var result = results[0];
        if (!result.Succeeded || result.ObjRef is null)
            return null;

        if (ObjRef.Parse(result.ObjRef) is not StandardObjRef objRef)
            throw new NdrFormatException("QueryInterface returned a non-standard object reference.");

        // The same object always has a single OXID, so a sibling bind on this connection is always correct.
        return await BindSiblingAsync(objRef.Std, interfaceIid, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="CallAsync"/> for a method whose reply can hold object references. Only a call that has not been sent can be cancelled: an
    /// abandoned reply is discarded, and a reference nobody releases costs the server handles and memory until DCOM's timeout, minutes after
    /// the session ends. Such calls are quick to answer, and the RPC timeout still applies. The caller's next step sees the cancellation.
    /// </summary>
    public Task<NdrReader> CallReturningReferencesAsync(ushort opnum, Action<NdrWriter> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CallAsync(opnum, body, CancellationToken.None);
    }

    /// <summary>Calls <paramref name="opnum"/> with ORPCTHIS followed by whatever <paramref name="body"/> writes, and returns the reply after ORPCTHAT.</summary>
    public async Task<NdrReader> CallAsync(ushort opnum, Action<NdrWriter> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var writer = new NdrWriter();
        OrpcHeader.WriteThis(writer, Guid.NewGuid());
        body(writer);
        var reply = await RpcClient.CallAsync(_context, opnum, writer.ToArray(), Ipid, cancellationToken)
            .ConfigureAwait(false);
        var reader = new NdrReader(reply);
        OrpcHeader.ReadThat(reader);
        return reader;
    }

    /// <summary>
    /// Reads the trailing HRESULT every WMI ORPC reply ends with, after its own [out] parameters have been
    /// read, and throws <see cref="WmiException"/> if its severity bit is set (MS-WMI 2.2.3).
    /// </summary>
    public static int ReadStatus(NdrReader reader, string methodName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var status = reader.ReadInt32();
        if (!reader.IsAtEnd)
            throw new NdrFormatException($"{methodName}: unexpected data after the status.");

        WmiStatus.ThrowIfError(status, $"{methodName} failed");
        return status;
    }

    public async ValueTask DisposeAsync()
    {
        await _reference.DisposeAsync().ConfigureAwait(false);
        await _lease.ReleaseAsync().ConfigureAwait(false);
    }
}
