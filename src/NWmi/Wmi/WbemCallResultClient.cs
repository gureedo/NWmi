using NWmi.Ndr.Ole;
using NWmi.Wmio;

namespace NWmi.Wmi;

/// <summary>
/// IWbemCallResult (MS-WMI 3.1.4.5): the handle a semisynchronous call (<see cref="WbemGenericFlags.ReturnImmediately"/>)
/// returns, used to fetch the real result once it is ready.
/// </summary>
internal sealed class WbemCallResultClient : IAsyncDisposable
{
    private const ushort OpGetResultObject = 3;
    private const ushort OpGetResultString = 4;
    private const ushort OpGetResultServices = 5;
    private const ushort OpGetCallStatus = 6;

    private readonly WmiOrpcConnection _connection;

    internal WbemCallResultClient(WmiOrpcConnection connection)
    {
        _connection = connection;
    }

    /// <summary>GetResultObject (opnum 3): the IWbemClassObject a GetObject/PutInstance/... call produced.</summary>
    public async Task<WmioObjectBlock?> GetResultObjectAsync(int timeoutMilliseconds = WbemTimeout.Infinite,
        CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallAsync(OpGetResultObject, writer => writer.WriteInt32(timeoutMilliseconds),
                cancellationToken)
            .ConfigureAwait(false);
        var pointer = reader.ReadMInterfacePointer();
        ThrowIfTimedOut(WmiOrpcConnection.ReadStatus(reader, "GetResultObject"), "GetResultObject");
        return pointer.IsNull || pointer.Value is null ? null : WmiClassObjectCodec.Decode(pointer.Value);
    }

    /// <summary>GetResultString (opnum 4): the string result of a call that produces one (e.g. an object path).</summary>
    public async Task<string?> GetResultStringAsync(int timeoutMilliseconds = WbemTimeout.Infinite,
        CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallAsync(OpGetResultString, writer => writer.WriteInt32(timeoutMilliseconds),
                cancellationToken)
            .ConfigureAwait(false);
        var value = reader.ReadBstr();
        ThrowIfTimedOut(WmiOrpcConnection.ReadStatus(reader, "GetResultString"), "GetResultString");
        return value.Value;
    }

    /// <summary>GetResultServices (opnum 5): the IWbemServices an OpenNamespace call produced.</summary>
    public async Task<WbemServicesClient?> GetResultServicesAsync(int timeoutMilliseconds = WbemTimeout.Infinite,
        CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallReturningReferencesAsync(OpGetResultServices,
                writer => writer.WriteInt32(timeoutMilliseconds), cancellationToken)
            .ConfigureAwait(false);
        var pointer = reader.ReadMInterfacePointer();
        ThrowIfTimedOut(WmiOrpcConnection.ReadStatus(reader, "GetResultServices"), "GetResultServices");
        return await WmiOrpcConnection
            .ConnectOrBindAsync(_connection, pointer.Value, WmiIds.IidWbemServices,
                static connection => new WbemServicesClient(connection))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// GetCallStatus (opnum 6): whether the call has completed yet. Returns <see cref="WbemStatus.Timedout"/>
    /// while still pending (not an error: see <see cref="WmiStatus.IsError(int)"/>).
    /// </summary>
    public async Task<int> GetCallStatusAsync(int timeoutMilliseconds = WbemTimeout.NoWait,
        CancellationToken cancellationToken = default)
    {
        var reader = await _connection.CallAsync(OpGetCallStatus, writer => writer.WriteInt32(timeoutMilliseconds),
                cancellationToken)
            .ConfigureAwait(false);
        var callStatus = reader.ReadInt32(); // plStatus: the call's own completion status, once it has one
        var status = WmiOrpcConnection.ReadStatus(reader, "GetCallStatus");
        return status == (int)WbemStatus.Timedout ? status : callStatus;
    }

    /// <summary>WBEM_S_TIMEDOUT is not an error status, but it means the call is still running and there is no result yet.</summary>
    private static void ThrowIfTimedOut(int status, string methodName)
    {
        if (status == (int)WbemStatus.Timedout)
            throw new WmiException(status, $"{methodName} timed out: the call is still running on the server.");
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
