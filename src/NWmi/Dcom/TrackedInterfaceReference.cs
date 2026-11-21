namespace NWmi.Dcom;

/// <summary>
/// An interface reference the client holds, released automatically through <see cref="RemUnknown2.ReleaseAsync"/>
/// when disposed. Obtained from <see cref="ExporterConnection.Track"/>. Disposing more than once, or after the
/// owning connection is gone, is harmless: the second release is skipped, and a release that fails because the
/// connection already closed is swallowed, since the server will reclaim the object on its own once pinging stops.
/// </summary>
internal sealed class TrackedInterfaceReference : IAsyncDisposable
{
    private readonly ExporterConnection _owner;
    private readonly StdObjRef _std;
    private bool _released;

    internal TrackedInterfaceReference(ExporterConnection owner, StdObjRef std)
    {
        _owner = owner;
        _std = std;
    }

    /// <summary>The interface this reference is for.</summary>
    public Guid Ipid => _std.Ipid;

    /// <summary>The object (not the interface) the server keeps alive while it is pinged.</summary>
    public ulong Oid => _std.Oid;

    public async ValueTask DisposeAsync()
    {
        if (_released)
            return;

        _released = true;
        _owner.Untrack(this);
        try
        {
            await _owner.RemUnknown.ReleaseAsync([new RemInterfaceRef(_std.Ipid, _std.PublicRefs, 0)]).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection may already be unusable (e.g. closed, or the owner is being disposed) in ways that
            // surface as a DcomException, an RpcException, or a lower-level I/O failure depending on exactly
            // when things broke. Nothing more we can do from here either way, and the server reclaims the
            // object on its own once it stops receiving pings.
        }
    }
}
