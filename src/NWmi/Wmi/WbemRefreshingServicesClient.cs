using System.Text;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Wmio;

namespace NWmi.Wmi;

/// <summary>_WBEM_REFRESHER_ID (MS-WMI 2.2.21): who is asking for refreshing services.</summary>
internal sealed record WbemRefresherId(string MachineName, uint ProcessId, Guid RefresherId)
{
    public static WbemRefresherId Create() => new(Environment.MachineName, (uint)Environment.ProcessId, Guid.NewGuid());

    internal void Write(NdrWriter writer) =>
        writer.Struct(w =>
        {
            // [string] LPSTR: a unique pointer to a conformant varying array of single-byte characters.
            w.WriteUniquePointer(MachineName, static (inner, name) =>
            {
                var bytes = Encoding.Latin1.GetBytes(name + "\0");
                inner.WriteMaxCount(bytes.Length);
                inner.WriteUInt32(0);
                inner.WriteUInt32((uint)bytes.Length);
                inner.WriteBytes(bytes);
            });
            w.WriteUInt32(ProcessId);
            w.WriteGuid(RefresherId);
        });
}

/// <summary>WBEM_REFRESH_TYPE (MS-WMI 2.2.25).</summary>
internal static class WbemRefreshType
{
    public const int Invalid = 0;
    public const int Remote = 3;
    public const int NonHiPerf = 6;
}

/// <summary>
/// _WBEM_REFRESH_INFO (MS-WMI 2.2.20). <see cref="Refresher"/> is set for <see cref="WbemRefreshType.Remote"/>; for
/// <see cref="WbemRefreshType.NonHiPerf"/> only <see cref="Namespace"/> and <see cref="Template"/> are.
/// </summary>
internal sealed record WbemRefreshInfo(
    int Type,
    WbemRemoteRefresherClient? Refresher,
    WmioObjectBlock? Template,
    Guid RefresherGuid,
    string? Namespace,
    int CancelId,
    int HResult);

/// <summary>
/// IWbemRefreshingServices (MS-WMI 3.1.4.12), obtained by querying an IWbemServices for it: registers the
/// objects and enumerations to refresh with a server-side refresher. Whether a class qualifies is the provider's
/// call: only high-performance providers (the <c>Win32_Perf*</c> classes) do, anything else comes back as
/// <see cref="WbemRefreshType.NonHiPerf"/> and has to be read the ordinary way.
/// </summary>
internal sealed class WbemRefreshingServicesClient : IAsyncDisposable
{
    private const ushort OpAddObjectToRefresher = 3;
    private const ushort OpAddEnumToRefresher = 5;
    private const ushort OpGetRemoteRefresher = 7;

    /// <summary>The client refresher version to announce (MS-WMI: SHOULD be 2).</summary>
    private const uint ClientVersion = 2;

    private readonly WmiOrpcConnection _connection;

    internal WbemRefreshingServicesClient(WmiOrpcConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Set to log the parsed reply shape while the layout is being verified.</summary>
    internal static Action<string>? DiagnosticSink { get; set; }

    /// <summary>AddObjectToRefresher (opnum 3): registers the instance at <paramref name="objectPath"/>.</summary>
    public Task<WbemRefreshInfo> AddObjectAsync(
        WbemRefresherId id, string objectPath, WmiContext? context = null,
        CancellationToken cancellationToken = default) =>
        AddAsync(OpAddObjectToRefresher, "AddObjectToRefresher", id, objectPath, context, cancellationToken);

    /// <summary>AddEnumToRefresher (opnum 5): registers every instance of <paramref name="className"/>.</summary>
    public Task<WbemRefreshInfo> AddEnumAsync(
        WbemRefresherId id, string className, WmiContext? context = null,
        CancellationToken cancellationToken = default) =>
        AddAsync(OpAddEnumToRefresher, "AddEnumToRefresher", id, className, context, cancellationToken);

    private async Task<WbemRefreshInfo> AddAsync(
        ushort opnum, string methodName, WbemRefresherId id, string path, WmiContext? context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(path);
        var reader = await _connection.CallReturningReferencesAsync(opnum, writer =>
        {
            id.Write(writer);
            writer.WriteConformantVaryingString(path);
            writer.WriteInt32(0); // lFlags
            WmiContext.WriteParameter(writer, context);
            writer.WriteUInt32(ClientVersion);
        }, cancellationToken).ConfigureAwait(false);

        var info = ReadRefreshInfo(reader);
        reader.ReadUInt32(); // pdwSvrRefrVersion
        WmiOrpcConnection.ReadStatus(reader, methodName);

        DiagnosticSink?.Invoke(info.ToString());
        return await info.ConnectAsync(_connection).ConfigureAwait(false);
    }

    /// <summary>GetRemoteRefresher (opnum 7): the refresher the server keeps for <paramref name="id"/>.</summary>
    public async Task<(WbemRemoteRefresherClient Refresher, Guid Guid)> GetRemoteRefresherAsync(
        WbemRefresherId id, CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallReturningReferencesAsync(OpGetRemoteRefresher, writer =>
        {
            id.Write(writer);
            writer.WriteInt32(0);
            writer.WriteUInt32(ClientVersion);
        }, cancellationToken).ConfigureAwait(false);
        var pointer = reader.ReadMInterfacePointer();
        var guid = reader.ReadGuid();
        reader.ReadUInt32();
        WmiOrpcConnection.ReadStatus(reader, "GetRemoteRefresher");
        var refresher = await WmiOrpcConnection.ConnectOrBindAsync(_connection, pointer.Value,
                WmiIds.IidWbemRemoteRefresher, static c => new WbemRemoteRefresherClient(c))
            .ConfigureAwait(false) ?? throw new NdrFormatException("GetRemoteRefresher returned no refresher.");
        return (refresher, guid);
    }

    private static RawRefreshInfo ReadRefreshInfo(NdrReader reader) =>
        reader.Struct(r =>
        {
            var type = r.ReadInt32();
            var tag = r.ReadInt32(); // the non-encapsulated union repeats its discriminant
            if (tag != type)
                throw new NdrFormatException($"_WBEM_REFRESH_INFO: type {type} but union discriminant {tag}.");

            NdrDeferred<byte[]?>? refresher = null;
            NdrDeferred<byte[]?>? template = null;
            NdrDeferred<string?>? ns = null;
            var guid = Guid.Empty;
            var hresult = 0;
            switch (type)
            {
                case WbemRefreshType.Remote:
                    refresher = r.ReadMInterfacePointer();
                    template = r.ReadMInterfacePointer();
                    guid = r.ReadGuid();
                    break;
                case WbemRefreshType.NonHiPerf:
                    ns = r.ReadUniquePointer<string?>(static p => p.ReadConformantVaryingString());
                    template = r.ReadMInterfacePointer();
                    break;
                default:
                    hresult = r.ReadInt32();
                    break;
            }

            var cancelId = r.ReadInt32();
            return new RawRefreshInfo(type, refresher, template, guid, ns, cancelId, hresult);
        });

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>A <see cref="WbemRefreshInfo"/> whose interface pointers are still raw OBJREFs.</summary>
    private sealed record RawRefreshInfo(
        int Type,
        NdrDeferred<byte[]?>? Refresher,
        NdrDeferred<byte[]?>? Template,
        Guid Guid,
        NdrDeferred<string?>? Namespace,
        int CancelId,
        int HResult)
    {
        public async Task<WbemRefreshInfo> ConnectAsync(WmiOrpcConnection connection)
        {
            var refresher = Refresher is { IsNull: false } r
                ? await WmiOrpcConnection.ConnectOrBindAsync(connection, r.Value, WmiIds.IidWbemRemoteRefresher,
                    static c => new WbemRemoteRefresherClient(c)).ConfigureAwait(false)
                : null;
            var template = Template is { IsNull: false } t && t.Value is not null
                ? WmiClassObjectCodec.Decode(t.Value)
                : null;
            return new WbemRefreshInfo(Type, refresher, template, Guid,
                Namespace is { IsNull: false } n ? n.Value : null,
                CancelId, HResult);
        }

        public override string ToString() =>
            $"type={Type} cancelId={CancelId} guid={Guid} hr=0x{HResult:X8} refresher={Refresher?.ReferentId} template={Template?.ReferentId}";
    }
}
