using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;

namespace NWmi.Wmi;

/// <summary>
/// IWbemLevel1Login (MS-WMI 3.1.4.1): the WMI protocol's entry point. Activate CLSID_WbemLevel1Login with
/// <see cref="ConnectAsync"/>, then call <see cref="NtlmLoginAsync"/> to get an <see cref="WbemServicesClient"/>
/// for a namespace.
/// </summary>
internal sealed class WbemLevel1LoginClient : IAsyncDisposable
{
    private const ushort OpNtlmLogin = 6;

    private readonly WmiOrpcConnection _connection;

    private WbemLevel1LoginClient(WmiOrpcConnection connection)
    {
        _connection = connection;
    }

    internal static WbemLevel1LoginClient FromConnection(WmiOrpcConnection connection) => new(connection);

    public static async Task<WbemLevel1LoginClient> ConnectAsync(
        string host, RpcSecurity? security = null, int port = DcomConstants.EndpointMapperPort,
        bool keepAlive = false, TimeSpan? keepAlivePeriod = null, RpcClientOptions? callOptions = null,
        CancellationToken cancellationToken = default)
    {
        var dcomClient = await DcomClient.ConnectAsync(host, security, port, cancellationToken).ConfigureAwait(false);
        var handedOver = false;
        try
        {
            var activation = await dcomClient
                .Activator.CreateInstanceAsync(WmiIds.ClsidWbemLevel1Login, [WmiIds.IidWbemLevel1Login],
                    cancellationToken)
                .ConfigureAwait(false);
            var std = activation.GetStandardObjRef(0).Std;
            var connection = await WmiOrpcConnection
                .FromActivationAsync(activation.Reply, std, WmiIds.IidWbemLevel1Login, security, cancellationToken,
                    preferredHost: host, pingClient: keepAlive ? dcomClient : null, pingPeriod: keepAlivePeriod,
                    pingReconnect: ct => DcomClient.ConnectAsync(host, security, port, ct), callOptions: callOptions)
                .ConfigureAwait(false);
            handedOver = keepAlive;
            return new WbemLevel1LoginClient(connection);
        }
        finally
        {
            // The connection to the endpoint mapper has done its job (activation); the login object itself
            // lives on the exporter connection WmiOrpcConnection just opened. With keep-alive that exporter connection
            // keeps this one open to ping through, and disposes it.
            if (!handedOver)
                await dcomClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// NTLMLogin (opnum 6, MS-WMI 3.1.4.1.4): connects to <paramref name="networkResource"/> (a namespace
    /// path, MS-WMI 2.2.2, e.g. <c>root\cimv2</c>) and returns its IWbemServices.
    /// </summary>
    public async Task<WbemServicesClient> NtlmLoginAsync(
        string networkResource, string? preferredLocale = null, WmiContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networkResource);
        var reader = await _connection.CallReturningReferencesAsync(OpNtlmLogin, writer =>
        {
            WriteLpwstr(writer, networkResource);
            WriteLpwstr(writer, preferredLocale);
            writer.WriteInt32(0); // lFlags: MUST be 0 (MS-WMI 3.1.4.1.4)
            WmiContext.WriteParameter(writer, context);
        }, cancellationToken).ConfigureAwait(false);

        var namespacePointer = reader.ReadMInterfacePointer();
        WmiOrpcConnection.ReadStatus(reader, "NTLMLogin");
        if (namespacePointer.IsNull || namespacePointer.Value is null)
            throw new NdrFormatException("NTLMLogin succeeded but returned no IWbemServices interface.");

        if (ObjRef.Parse(namespacePointer.Value) is not StandardObjRef objRef)
            throw new NdrFormatException("NTLMLogin's IWbemServices was not a standard object reference.");

        var servicesConnection = await WmiOrpcConnection
            .ConnectOrBindAsync(_connection, objRef, WmiIds.IidWbemServices, _connection.Security, CancellationToken.None)
            .ConfigureAwait(false);
        return new WbemServicesClient(servicesConnection);
    }

    /// <summary>A <c>[unique, string] wchar_t*</c>: a unique pointer to a conformant-varying string, or null.</summary>
    internal static void WriteLpwstr(NdrWriter writer, string? value) =>
        writer.WriteUniquePointer(value is not null, value is null ? null : w => w.WriteConformantVaryingString(value));

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
