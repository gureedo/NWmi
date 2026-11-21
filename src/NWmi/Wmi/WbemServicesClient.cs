using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Wmio;

namespace NWmi.Wmi;

/// <summary>What <see cref="WbemServicesClient.OpenNamespaceAsync"/> returns (MS-WMI 3.1.4.3.1).</summary>
internal sealed record WbemOpenNamespaceResult(WbemServicesClient? WorkingNamespace, WbemCallResultClient? CallResult);

/// <summary>What <see cref="WbemServicesClient.GetObjectAsync"/> returns (MS-WMI 3.1.4.3.4).</summary>
internal sealed record WbemGetObjectResult(WmioObjectBlock? Object, WbemCallResultClient? CallResult);

/// <summary>What <see cref="WbemServicesClient.ExecMethodAsync"/> returns (MS-WMI 3.1.4.3.22).</summary>
internal sealed record WbemExecMethodResult(WmioObjectBlock? OutParameters, WbemCallResultClient? CallResult);

/// <summary>
/// IWbemServices (MS-WMI 3.1.4.3): queries, retrieves, creates, updates and deletes CIM classes and instances
/// in one namespace. Obtained from <see cref="WbemLevel1LoginClient.NtlmLoginAsync"/> or
/// <see cref="OpenNamespaceAsync"/>.
/// </summary>
internal sealed class WbemServicesClient : IAsyncDisposable
{
    private const ushort OpOpenNamespace = 3;
    private const ushort OpGetObject = 6;
    private const ushort OpCreateClassEnum = 12;
    private const ushort OpPutInstance = 14;
    private const ushort OpDeleteInstance = 16;
    private const ushort OpCreateInstanceEnum = 18;
    private const ushort OpExecQuery = 20;
    private const ushort OpExecNotificationQuery = 22;
    private const ushort OpExecMethod = 24;

    private readonly WmiOrpcConnection _connection;

    internal WbemServicesClient(WmiOrpcConnection connection)
    {
        _connection = connection;
    }

    /// <summary>The keeper pinging this namespace's exporter, or null; exposed for tests.</summary>
    internal PingKeeper? Pinger => _connection.Pinger;

    /// <summary>OpenNamespace (opnum 3): opens a namespace below this one (e.g. <c>root</c> to <c>root\cimv2</c>).</summary>
    public async Task<WbemOpenNamespaceResult> OpenNamespaceAsync(
        string relativeNamespace, WbemGenericFlags flags = 0, WmiContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativeNamespace);
        var reader = await _connection.CallReturningReferencesAsync(OpOpenNamespace, writer =>
        {
            writer.WriteBstr(relativeNamespace);
            writer.WriteInt32((int)flags);
            WmiContext.WriteParameter(writer, context);
            writer.WriteOutInterfaceSlot(); // ppWorkingNamespace
            writer.WriteOutInterfaceSlot(); // ppResult
        }, cancellationToken).ConfigureAwait(false);

        var namespacePointer = reader.ReadOutInterfaceSlot();
        var resultPointer = reader.ReadOutInterfaceSlot();
        WmiOrpcConnection.ReadStatus(reader, "OpenNamespace");

        return new WbemOpenNamespaceResult(
            await ConnectServicesAsync(namespacePointer.Value).ConfigureAwait(false),
            await ConnectCallResultAsync(resultPointer.Value).ConfigureAwait(false));
    }

    /// <summary>GetObject (opnum 6): retrieves a CIM class or instance by its CIM path (MS-WMI 2.2.2).</summary>
    public async Task<WbemGetObjectResult> GetObjectAsync(
        string? objectPath, WbemGenericFlags flags = 0, WmiContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var semisynchronous = (flags & WbemGenericFlags.ReturnImmediately) != 0;

        var reader = await (semisynchronous
            ? _connection.CallReturningReferencesAsync(OpGetObject, Body, cancellationToken)
            : _connection.CallAsync(OpGetObject, Body, cancellationToken)).ConfigureAwait(false);

        var objectPointer = reader.ReadOutInterfaceSlot();
        var resultPointer = reader.ReadOutInterfaceSlot();
        WmiOrpcConnection.ReadStatus(reader, "GetObject");

        return new WbemGetObjectResult(
            objectPointer.IsNull || objectPointer.Value is null
                ? null
                : WmiClassObjectCodec.Decode(objectPointer.Value),
            await ConnectCallResultAsync(resultPointer.Value).ConfigureAwait(false));

        void Body(NdrWriter writer)
        {
            writer.WriteBstr(objectPath);
            writer.WriteInt32((int)flags);
            WmiContext.WriteParameter(writer, context);
            writer.WriteOutInterfaceSlot(); // ppObject
            if (semisynchronous)
                writer.WriteOutInterfaceSlot(); // ppCallResult
            else
                // Null: given a slot, Windows fills it even on a synchronous call that also returns the object, which would only cost
                // a reference to bind and release. With no reference in the reply, the call can wait on a slow provider and stay cancellable.
                writer.WriteUInt32(0);
        }
    }

    /// <summary>PutInstance (opnum 14): creates or updates a CIM instance. Returns the IWbemCallResult for a semisynchronous call, or null.</summary>
    public async Task<WbemCallResultClient?> PutInstanceAsync(
        WmioObjectBlock instance, WbemGenericFlags flags = 0, WmiContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var reader = await _connection.CallReturningReferencesAsync(OpPutInstance, writer =>
        {
            WmiClassObjectCodec.WriteParameter(writer, instance);
            writer.WriteInt32((int)flags);
            WmiContext.WriteParameter(writer, context);
            writer.WriteOutInterfaceSlot(); // ppCallResult
        }, cancellationToken).ConfigureAwait(false);

        var resultPointer = reader.ReadOutInterfaceSlot();
        WmiOrpcConnection.ReadStatus(reader, "PutInstance");
        return await ConnectCallResultAsync(resultPointer.Value).ConfigureAwait(false);
    }

    /// <summary>DeleteInstance (opnum 16): deletes the CIM instance at <paramref name="objectPath"/>.</summary>
    public async Task<WbemCallResultClient?> DeleteInstanceAsync(
        string objectPath, WbemGenericFlags flags = 0, WmiContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectPath);
        var reader = await _connection.CallReturningReferencesAsync(OpDeleteInstance, writer =>
        {
            writer.WriteBstr(objectPath);
            writer.WriteInt32((int)flags);
            WmiContext.WriteParameter(writer, context);
            writer.WriteOutInterfaceSlot(); // ppCallResult
        }, cancellationToken).ConfigureAwait(false);

        var resultPointer = reader.ReadOutInterfaceSlot();
        WmiOrpcConnection.ReadStatus(reader, "DeleteInstance");
        return await ConnectCallResultAsync(resultPointer.Value).ConfigureAwait(false);
    }

    /// <summary>CreateInstanceEnum (opnum 18): enumerates every instance of <paramref name="superClass"/> and its subclasses.</summary>
    public Task<EnumWbemClassObjectClient> CreateInstanceEnumAsync(
        string superClass, WbemGenericFlags flags = 0, WmiContext? context = null,
        CancellationToken cancellationToken = default) =>
        CreateEnumAsync(OpCreateInstanceEnum, "CreateInstanceEnum", superClass, flags, context, cancellationToken);

    /// <summary>CreateClassEnum (opnum 12): enumerates every subclass of <paramref name="superClass"/> (or every class, if empty).</summary>
    public Task<EnumWbemClassObjectClient> CreateClassEnumAsync(
        string? superClass = null, WbemGenericFlags flags = 0, WmiContext? context = null,
        CancellationToken cancellationToken = default) =>
        CreateEnumAsync(OpCreateClassEnum, "CreateClassEnum", superClass, flags, context, cancellationToken);

    private async Task<EnumWbemClassObjectClient> CreateEnumAsync(
        ushort opnum, string methodName, string? classArgument, WbemGenericFlags flags, WmiContext? context,
        CancellationToken cancellationToken)
    {
        var reader = await _connection.CallReturningReferencesAsync(opnum, writer =>
        {
            writer.WriteBstr(classArgument);
            writer.WriteInt32((int)flags);
            WmiContext.WriteParameter(writer, context);
        }, cancellationToken).ConfigureAwait(false);

        var enumPointer = reader.ReadMInterfacePointer();
        WmiOrpcConnection.ReadStatus(reader, methodName);
        return await ConnectEnumAsync(enumPointer.Value).ConfigureAwait(false)
               ?? throw new NdrFormatException($"{methodName} succeeded but returned no enumerator.");
    }

    /// <summary>ExecQuery (opnum 20): runs a WQL query (MS-WMI 2.2.1) and returns an enumerator over the results.</summary>
    public Task<EnumWbemClassObjectClient> ExecQueryAsync(
        string query, WbemGenericFlags flags = 0, WmiContext? context = null,
        CancellationToken cancellationToken = default) =>
        ExecQueryCoreAsync(OpExecQuery, "ExecQuery", query, flags, context, cancellationToken);

    /// <summary>ExecNotificationQuery (opnum 22): the event-query counterpart of <see cref="ExecQueryAsync"/>.</summary>
    public Task<EnumWbemClassObjectClient> ExecNotificationQueryAsync(
        string query, WbemGenericFlags flags = 0, WmiContext? context = null,
        CancellationToken cancellationToken = default) =>
        ExecQueryCoreAsync(OpExecNotificationQuery, "ExecNotificationQuery", query, flags, context, cancellationToken);

    private async Task<EnumWbemClassObjectClient> ExecQueryCoreAsync(
        ushort opnum, string methodName, string query, WbemGenericFlags flags, WmiContext? context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var reader = await _connection.CallReturningReferencesAsync(opnum, writer =>
        {
            writer.WriteBstr("WQL");
            writer.WriteBstr(query);
            writer.WriteInt32((int)flags);
            WmiContext.WriteParameter(writer, context);
        }, cancellationToken).ConfigureAwait(false);

        var enumPointer = reader.ReadMInterfacePointer();
        WmiOrpcConnection.ReadStatus(reader, methodName);
        return await ConnectEnumAsync(enumPointer.Value).ConfigureAwait(false)
               ?? throw new NdrFormatException($"{methodName} succeeded but returned no enumerator.");
    }

    /// <summary>
    /// ExecMethod (opnum 24): calls <paramref name="methodName"/> on the object at <paramref name="objectPath"/>.
    /// <paramref name="inParameters"/> is normally built with <see cref="WmioInstance.Create"/> from the
    /// method's input signature (obtained by inspecting the class via <see cref="GetObjectAsync"/>).
    /// </summary>
    public async Task<WbemExecMethodResult> ExecMethodAsync(
        string objectPath,
        string methodName,
        WmioObjectBlock? inParameters = null,
        WbemGenericFlags flags = 0,
        WmiContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectPath);
        ArgumentNullException.ThrowIfNull(methodName);
        var reader = await _connection.CallReturningReferencesAsync(OpExecMethod, writer =>
        {
            writer.WriteBstr(objectPath);
            writer.WriteBstr(methodName);
            writer.WriteInt32((int)flags);
            WmiContext.WriteParameter(writer, context);
            WmiClassObjectCodec.WriteParameter(writer, inParameters);
            writer.WriteOutInterfaceSlot(); // ppOutParams
            writer.WriteOutInterfaceSlot(); // ppCallResult
        }, cancellationToken).ConfigureAwait(false);

        var outParamsPointer = reader.ReadOutInterfaceSlot();
        var resultPointer = reader.ReadOutInterfaceSlot();
        WmiOrpcConnection.ReadStatus(reader, "ExecMethod");

        return new WbemExecMethodResult(
            outParamsPointer.IsNull || outParamsPointer.Value is null
                ? null
                : WmiClassObjectCodec.Decode(outParamsPointer.Value),
            await ConnectCallResultAsync(resultPointer.Value).ConfigureAwait(false));
    }

    /// <summary>
    /// QueryInterface for IWbemRefreshingServices (MS-WMI 3.2.4.5). Null when the server does not offer it.
    /// </summary>
    public async Task<WbemRefreshingServicesClient?> TryGetRefreshingServicesAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = await _connection.QueryInterfaceAsync(WmiIds.IidWbemRefreshingServices, cancellationToken)
            .ConfigureAwait(false);
        return connection is null ? null : new WbemRefreshingServicesClient(connection);
    }

    private Task<WbemServicesClient?> ConnectServicesAsync(byte[]? objRefBytes) =>
        WmiOrpcConnection.ConnectOrBindAsync(_connection, objRefBytes, WmiIds.IidWbemServices,
            static connection => new WbemServicesClient(connection));

    private Task<WbemCallResultClient?>
        ConnectCallResultAsync(byte[]? objRefBytes) =>
        WmiOrpcConnection.ConnectOrBindAsync(_connection, objRefBytes, WmiIds.IidWbemCallResult,
            static connection => new WbemCallResultClient(connection));

    private Task<EnumWbemClassObjectClient?>
        ConnectEnumAsync(byte[]? objRefBytes) =>
        WmiOrpcConnection.ConnectOrBindAsync(_connection, objRefBytes, WmiIds.IidEnumWbemClassObject,
            static connection => new EnumWbemClassObjectClient(connection));

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
