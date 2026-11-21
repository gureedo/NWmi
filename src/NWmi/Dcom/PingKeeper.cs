using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>
/// Keeps a set of OIDs alive against one object resolver by pinging it periodically (MS-DCOM 3.1.2.2, 3.2.6.1).
/// <see cref="Add"/> the OID of each activated object (<c>StdObjRef.Oid</c>) you intend to keep for longer than
/// one ping period; <see cref="Remove"/> it once you are done with it, or dispose the keeper to stop pinging
/// and tell the resolver to drop every OID it still holds in one last call.
/// </summary>
/// <remarks>
/// One keeper corresponds to one entry of the client's "Resolver table" (MS-DCOM 3.2.6.1): it pings a single
/// object resolver for a single ping set. An application talking to several machines needs one keeper per
/// machine (per <see cref="ObjectExporter"/>).
/// </remarks>
internal sealed class PingKeeper : IAsyncDisposable
{
    /// <summary>MS-DCOM 3.1.2.2: the ping period SHOULD be exactly 2 minutes.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(2);

    private readonly Func<CancellationToken, Task<DcomClient>>? _reconnect;
    private readonly TimeSpan _period;
    private readonly SemaphoreSlim _pingGate = new(1, 1);
    private readonly Lock _startLock = new();
    private readonly HashSet<ulong> _wanted = [];

    // OIDs the resolver's set currently holds, as of the last successful ping
    private readonly HashSet<ulong> _confirmed = [];

    private ObjectExporter _exporter;

    // the connection opened after losing the first one, which this keeper does not own
    private DcomClient? _replacement;

    private bool _reconnectNeeded;
    private ulong _setId;
    private ushort _sequenceNumber = 1;
    private Task? _loop;
    private CancellationTokenSource? _stop;

    /// <param name="exporter">The object resolver to ping.</param>
    /// <param name="period">The time between pings.</param>
    /// <param name="reconnect">
    /// Opens a new connection to the same object resolver when a ping fails on anything but a fault from the server. The
    /// resolver's ping set outlives the connection. Without it, a lost connection stops the pings for good.
    /// </param>
    public PingKeeper(ObjectExporter exporter, TimeSpan? period = null,
        Func<CancellationToken, Task<DcomClient>>? reconnect = null)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        _exporter = exporter;
        _reconnect = reconnect;
        _period = period ?? DefaultPeriod;
        if (_period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), period, "The ping period must be positive.");
    }

    /// <summary>The failure from the most recent ping attempt, if any; cleared by the next successful one.</summary>
    public Exception? LastFailure { get; private set; }

    /// <summary>How many pings have succeeded so far.</summary>
    public int PingCount { get; private set; }

    /// <summary>Adds an OID to keep alive. The resolver learns about it on the next ping, not immediately.</summary>
    public void Add(ulong oid)
    {
        lock (_wanted) _wanted.Add(oid);

        EnsureLoopStarted();
    }

    /// <summary>Stops keeping an OID alive. It is dropped from the resolver's set on the next ping, not immediately.</summary>
    public void Remove(ulong oid)
    {
        lock (_wanted) _wanted.Remove(oid);
    }

    /// <summary>Pings immediately instead of waiting for the next scheduled ping.</summary>
    public async Task PingNowAsync(CancellationToken cancellationToken = default)
    {
        await _pingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PingLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pingGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is not null)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

            _stop.Dispose();
        }

        // Best effort: tell the resolver to drop every OID we were still holding now, instead of making it
        // wait out its own set timer, which runs three times longer than the ping period.
        if (_setId != 0 && _confirmed.Count > 0)
            try
            {
                await _exporter
                    .ComplexPingAsync(_setId, _sequenceNumber, [], [.. _confirmed], CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Nothing more to do; the resolver reclaims the set on its own once pings stop arriving.
            }

        if (_replacement is not null)
            await _replacement.DisposeAsync().ConfigureAwait(false);

        _pingGate.Dispose();
    }

    private void EnsureLoopStarted()
    {
        if (_loop is not null)
            return;

        lock (_startLock)
        {
            if (_loop is not null)
                return;

            _stop = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_stop.Token));
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_period, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _pingGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await PingLockedAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _pingGate.Release();
            }
        }
    }

    /// <summary>Does the actual ping; the caller holds <see cref="_pingGate"/>.</summary>
    private async Task PingLockedAsync(CancellationToken cancellationToken)
    {
        List<ulong> wanted;
        lock (_wanted) wanted = [.. _wanted];

        var toAdd = wanted.Where(oid => !_confirmed.Contains(oid)).ToList();
        var toRemove = _confirmed.Where(oid => !wanted.Contains(oid)).ToList();

        try
        {
            if (_reconnectNeeded && _reconnect is not null)
            {
                var replacement = await _reconnect(cancellationToken).ConfigureAwait(false);
                if (_replacement is not null)
                    await _replacement.DisposeAsync().ConfigureAwait(false);

                _replacement = replacement;
                _exporter = replacement.Exporter;
                _reconnectNeeded = false;
            }

            if (_setId == 0 || toAdd.Count > 0 || toRemove.Count > 0)
            {
                var result = await _exporter
                    .ComplexPingAsync(_setId, _sequenceNumber, toAdd, toRemove, cancellationToken)
                    .ConfigureAwait(false);
                _setId = result.SetId;
                _sequenceNumber++;
                foreach (var oid in toAdd)
                    _confirmed.Add(oid);

                foreach (var oid in toRemove)
                    _confirmed.Remove(oid);
            }
            else
                await _exporter.SimplePingAsync(_setId, cancellationToken).ConfigureAwait(false);

            LastFailure = null;
            PingCount++;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded for the caller to notice; the next scheduled ping (or a call to PingNowAsync) retries.
            LastFailure = ex;
            _reconnectNeeded = ex is not RpcFaultException;
        }
    }
}
