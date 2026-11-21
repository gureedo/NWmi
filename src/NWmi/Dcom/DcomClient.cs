using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>
/// A connection to one machine's RPC endpoint mapper (port 135 by default), used to query its object resolver
/// and to activate objects on it (MS-DCOM 3.2.4.1.1). IObjectExporter is bound first, then IRemoteSCMActivator
/// is added with a separate alter_context, so both share whatever security the connection was opened with;
/// MS-DCOM 3.2.4.1.1.1 says a client need not authenticate the ServerAlive2 call, but nothing forbids it.
/// </summary>
/// <remarks>
/// A Windows endpoint mapper rejects IRemoteSCMActivator (and, symmetrically, IObjectExporter) with
/// bind_ack's ProviderRejection/ProposedTransferSyntaxesNotSupported when it is the second presentation
/// context offered in one bind PDU alongside the other activation interface (whichever interface is listed second is
/// rejected, so it is specific to offering more than one context in a single bind to this endpoint, not to either interface itself).
/// Binding one interface and then alter_context-ing in the other avoids this.
/// </remarks>
internal sealed class DcomClient : IAsyncDisposable
{
    private DcomClient(RpcClient rpcClient, ObjectExporter exporter, RemoteScmActivator activator)
    {
        RpcClient = rpcClient;
        Exporter = exporter;
        Activator = activator;
    }

    /// <summary>The object resolver's IObjectExporter on this connection, e.g. for a <see cref="PingKeeper"/>.</summary>
    internal ObjectExporter Exporter { get; }

    /// <summary>The IRemoteSCMActivator on this connection, which creates objects of a class on the host.</summary>
    internal RemoteScmActivator Activator { get; }

    /// <summary>The underlying RPC connection, exposed so other DCOM interfaces can be added with <see cref="RpcClient.AlterContextAsync"/>.</summary>
    public RpcClient RpcClient { get; }

    public static async Task<DcomClient> ConnectAsync(
        string host, RpcSecurity? security = null, int port = DcomConstants.EndpointMapperPort, CancellationToken cancellationToken = default)
    {
        var rpcClient = await RpcClient.ConnectTcpAsync(host, port, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            return await FromRpcClientAsync(rpcClient, security, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await rpcClient.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Binds IObjectExporter and adds IRemoteSCMActivator with alter_context on an already-connected
    /// <see cref="RpcClient"/>. Exposed for testing against something other than a real TCP connection;
    /// <see cref="ConnectAsync"/> is the normal entry point.
    /// </summary>
    internal static async Task<DcomClient> FromRpcClientAsync(
        RpcClient rpcClient, RpcSecurity? security, CancellationToken cancellationToken)
    {
        var exporterContexts = await rpcClient.BindAsync(
            [ObjectExporter.Interface], security, cancellationToken).ConfigureAwait(false);
        var activatorContexts = await rpcClient.AlterContextAsync(
            [DcomIds.RemoteScmActivator], cancellationToken).ConfigureAwait(false);
        return new DcomClient(rpcClient, new ObjectExporter(rpcClient, exporterContexts[0]), new RemoteScmActivator(rpcClient, activatorContexts[0]));
    }

    public ValueTask DisposeAsync() => RpcClient.DisposeAsync();
}
