using Microsoft.Extensions.Logging;
using NWmi.Dcom;
using NWmi.Rpc;

namespace NWmi.Options;

/// <summary>Settings for a <see cref="WmiConnection"/>: namespace, port, authentication level, timeouts and logging.</summary>
public sealed class WmiConnectionOptions
{
    /// <summary>The namespace to connect to, e.g. <c>root\cimv2</c> (the default).</summary>
    public string Namespace { get; set; } = @"root\cimv2";

    /// <summary>The remote host's RPC endpoint mapper port. 135 (the default) unless it was moved.</summary>
    public int Port { get; set; } = DcomConstants.EndpointMapperPort;

    /// <summary>
    /// The RPC authentication level. Defaults to packet privacy (sealed and signed), which MS-DCOM says every
    /// hardened server requires for activation and MS-WMI recommends for namespaces marked RequiresEncryption.
    /// </summary>
    public RpcAuthLevel AuthLevel { get; set; } = RpcAuthLevel.PacketPrivacy;

    /// <summary>
    /// How long a single WMI method call (login, a query batch, a method invocation, ...) may take before the
    /// server treats it as timed out (MS-WMI's own <c>lTimeout</c> parameter, not a .NET cancellation timeout).
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the client waits for the reply to one DCOM call before giving up on it with a <see cref="TimeoutException"/>. The client then
    /// orphans the call and drops the connection, as Windows clients do, and the next call opens a new one; the server may still run
    /// the call to its end. Methods, <see cref="WmiConnection.PutInstanceAsync"/> and <see cref="WmiConnection.DeleteInstanceAsync"/> may run for longer:
    /// they are waited for in shorter calls until they finish or the caller cancels. Null (the default) means 60 seconds, or
    /// <see cref="Timeout"/> plus 30 seconds when that is longer, so the server's own timeout comes first. Not used over WinRM.
    /// </summary>
    public TimeSpan? RpcTimeout { get; set; }

    /// <summary>
    /// The context every call on a new connection starts with (see <see cref="WmiConnection.Context"/>), e.g. the 32-bit or 64-bit
    /// registry view. Not sent with the login itself. Not available over WinRM.
    /// </summary>
    public WmiCallContext? Context { get; set; }

    /// <summary>
    /// Keep the session's objects alive on the server by pinging its object resolver (MS-DCOM 3.1.2.2), through one more connection to the
    /// endpoint mapper port that stays open for the life of the connection. A server that follows the specification may otherwise collect
    /// objects it has not heard about for about 6 minutes; the Windows 11 and Server 2025 hosts tried did not. A failed ping is not an error.
    /// Default true; turn it off if that extra connection is a problem. Not used over WinRM.
    /// </summary>
    public bool KeepAlive { get; set; } = true;

    /// <summary>The time between pings when <see cref="KeepAlive"/> is on. The specification says 2 minutes, which is the default.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Passed to NTLMLogin as <c>wszPreferredLocale</c> (MS-WMI 2.2.29); null lets the server choose.</summary>
    public string? PreferredLocale { get; set; }

    /// <summary>How many objects <see cref="WmiConnection.QueryAsync"/> fetches per round trip.</summary>
    public uint BatchSize { get; set; } = 25;

    /// <summary>
    /// Prefer the optimized ObjectArray enumeration (MS-WMI 4.2.2.3) when the server offers it, instead of
    /// always fetching one marshaled interface pointer per object. On by default.
    /// </summary>
    public bool UseSmartEnumeration { get; set; } = true;

    /// <summary>
    /// Where diagnostic messages (connecting, opening a namespace, running a query, the SPNEGO and Kerberos negotiation, ...)
    /// are logged, at Debug level. Defaults to no logging.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// WinRM only (<c>WmiConnection.ConnectWinRmAsync</c>): the listener's port; 0 (the default) means 5985, or 5986 with
    /// <see cref="WinRmUseHttps"/>.
    /// </summary>
    public int WinRmPort { get; set; }

    /// <summary>WinRM only: use HTTPS (TLS) instead of HTTP with NTLM message encryption.</summary>
    public bool WinRmUseHttps { get; set; }

    /// <summary>WinRM only: accept any server certificate (a self-signed listener certificate, say). Off by default.</summary>
    public bool WinRmIgnoreCertificateErrors { get; set; }

    internal int TimeoutMilliseconds => checked((int)Timeout.TotalMilliseconds);

    internal RpcClientOptions EffectiveRpcClientOptions => new() { Timeout = EffectiveRpcTimeout };

    internal TimeSpan EffectiveRpcTimeout =>
        RpcTimeout ?? (Timeout == System.Threading.Timeout.InfiniteTimeSpan
            ? System.Threading.Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(Math.Max(60, Timeout.TotalSeconds + 30)));
}
