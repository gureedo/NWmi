using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NWmi.Auth;
using NWmi.Auth.Kerberos;
using NWmi.Options;
using NWmi.Rpc;
using NWmi.WinRm;
using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi;

/// <summary>
/// A connection to one WMI namespace on one host: the public entry point built on
/// <see cref="NWmi.Wmi.WbemServicesClient"/> and the rest of <c>NWmi.Wmi</c>.
/// </summary>
/// <example>
/// <code>
/// await using var wmi = await WmiConnection.ConnectAsync(
///     "server01", new NetworkCredential("admin", "pw", "CORP"));
///
/// await foreach (var os in wmi.QueryAsync("SELECT Caption, Version FROM Win32_OperatingSystem"))
///     Console.WriteLine($"{os["Caption"]} {os["Version"]}");
/// </code>
/// </example>
public sealed class WmiConnection : IAsyncDisposable
{
    private readonly string? _host;
    private readonly string? _namespacePath;
    private readonly WinRmBackend? _winRm;
    private WmiCallContext? _context;

    internal WbemServicesClient Services { get; }

    internal WmiConnectionOptions Options { get; }

    /// <summary>
    /// The named values sent with every call made on this connection from now on (initially <see cref="WmiConnectionOptions.Context"/>);
    /// null or empty sends none. Calls already started, including a query that is still being read, keep the context they began with.
    /// A namespace opened with <see cref="OpenNamespaceAsync"/> starts with this connection's current context and then has its own.
    /// </summary>
    /// <exception cref="NotSupportedException">Setting a non-empty context on a WinRM connection.</exception>
    public WmiCallContext? Context
    {
        get => _context;
        set
        {
            if (_winRm is not null && value is { IsEmpty: false })
                throw new NotSupportedException("A call context needs DCOM; it is not available over WinRM.");

            _context = value;
        }
    }

    private WmiContext? WireContext() => _context?.ToWire();

    private WmiConnection(WbemServicesClient services, string? host, string? namespacePath,
        WmiConnectionOptions options)
    {
        Services = services;
        _host = host;
        _namespacePath = namespacePath;
        Options = options;
        _context = options.Context;
    }

    private WmiConnection(WinRmBackend winRm, string host, string namespacePath, WmiConnectionOptions options)
        : this((WbemServicesClient)null!, host, namespacePath, options)
    {
        // Every public operation checks _winRm first, so _services is never touched on this path.
        _winRm = winRm;
        if (options.Context is { IsEmpty: false })
            throw new NotSupportedException("A call context needs DCOM; it is not available over WinRM.");
    }

    /// <summary>
    /// Connects to <paramref name="host"/> over WinRM (WS-Management, HTTP port 5985 or HTTPS 5986) instead of DCOM, which
    /// needs only that one port. Authenticates with NTLM (a domain account works as <c>DOMAIN\user</c>); over plain HTTP
    /// every message is sealed with the NTLM session key, as Windows requires. The values are typed from the class definitions
    /// the server gives (see <see cref="WmiInstance.Class"/>). Not available over WinRM: refreshers and call contexts.
    /// </summary>
    public static Task<WmiConnection> ConnectWinRmAsync(
        string host, Credential credential, WmiConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return ConnectWinRmCoreAsync(host,
            (binding, _) => Task.FromResult<IRpcSecurityProvider>(
                new NtlmSecurityProvider(credential, new NtlmSecurityOptions { ChannelBindings = binding })),
            options, cancellationToken);
    }

    /// <summary>
    /// As <see cref="ConnectWinRmAsync(string, Credential, WmiConnectionOptions?, CancellationToken)"/>, but negotiates the
    /// mechanism with SPNEGO: Kerberos when <paramref name="spnego"/> (or DNS) says where to get a ticket (the service
    /// principal name defaults to <c>host/&lt;fqdn&gt;</c>, which the HTTP service class maps to), NTLM otherwise or if the
    /// listener picks it. The tickets are fetched at each login, so the host name should be the machine's DNS name.
    /// </summary>
    public static Task<WmiConnection> ConnectWinRmAsync(
        string host, Credential credential, SpnegoOptions spnego, WmiConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(spnego);
        var settings = spnego.Copy(options?.LoggerFactory);
        settings.Ntlm ??= new NtlmSecurityOptions();
        return ConnectWinRmCoreAsync(host,
            async (binding, ct) =>
            {
                settings.Ntlm.ChannelBindings = binding;
                return await SpnegoSecurityProvider.CreateAsync(host, credential, settings, ct).ConfigureAwait(false);
            },
            options, cancellationToken);
    }

    private static async Task<WmiConnection> ConnectWinRmCoreAsync(
        string host, Func<Func<byte[]?>, CancellationToken, Task<IRpcSecurityProvider>> providerFactory,
        WmiConnectionOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        options ??= new WmiConnectionOptions();
        var port = options.WinRmPort != 0 ? options.WinRmPort : options.WinRmUseHttps ? 5986 : 5985;
        var endpoint = new UriBuilder(options.WinRmUseHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, host, port,
            "/wsman").Uri;
        Log(options, $"Connecting to {endpoint} over WinRM.");
        var transport = new WsmanTransport(endpoint, providerFactory, options.Timeout + TimeSpan.FromSeconds(30),
            (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WsmanTransport>(),
            options.WinRmIgnoreCertificateErrors);
        try
        {
            var backend = await WinRmBackend.ConnectAsync(transport, host, options.Namespace, options.Timeout,
                options.BatchSize, cancellationToken).ConfigureAwait(false);
            return new WmiConnection(backend, host, options.Namespace, options);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an already-connected <see cref="WbemServicesClient"/>; exposed for testing against a fake transport.</summary>
    internal static WmiConnection FromServices(WbemServicesClient services, string? host, string? namespacePath,
        WmiConnectionOptions? options = null) =>
        new(services, host, namespacePath, options ?? new WmiConnectionOptions());

    /// <summary>
    /// Connects to <paramref name="host"/>, authenticates with NTLM using a plain password, and logs in to
    /// <see cref="WmiConnectionOptions.Namespace"/>.
    /// </summary>
    public static Task<WmiConnection> ConnectAsync(
        string host, NetworkCredential credential, WmiConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return ConnectAsync(
            host,
            new Credential(credential.UserName, credential.Password, credential.Domain),
            options,
            cancellationToken);
    }

    /// <summary>
    /// Connects to <paramref name="host"/>, authenticates with NTLM, and logs in to <see cref="WmiConnectionOptions.Namespace"/>.
    /// Accepting an <see cref="Credential"/> directly (rather than only a password-based <see cref="NetworkCredential"/>)
    /// is what lets a caller supply a domain and username already split via <see cref="Credential.Parse"/>.
    /// </summary>
    public static async Task<WmiConnection> ConnectAsync(
        string host, Credential credential, WmiConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credential);
        options ??= new WmiConnectionOptions();
        Log(options, $"Connecting to {host} as {credential.Domain}\\{credential.UserName}.");
        return await ConnectWithSecurityAsync(host, new NtlmSecurityProvider(credential), options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to <paramref name="host"/>, authenticates with Kerberos (wrapped in SPNEGO), and logs in to
    /// <see cref="WmiConnectionOptions.Namespace"/>. Unlike the NTLM overloads, this needs the KDC's address
    /// and the target's service principal name up front: obtaining a usable AP-REQ token requires a full
    /// AS/TGS exchange with the KDC <em>before</em> the RPC bind to <paramref name="host"/> even starts.
    /// </summary>
    public static async Task<WmiConnection> ConnectAsync(
        string host,
        KerberosCredential credential,
        string kdcHost,
        string servicePrincipalName,
        WmiConnectionOptions? options = null,
        int kdcPort = KerberosConstants.KdcPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(kdcHost);
        ArgumentNullException.ThrowIfNull(servicePrincipalName);
        options ??= new WmiConnectionOptions();
        Log(options, $"Negotiating Kerberos for {credential.UserName}@{credential.Realm} against {kdcHost}.");
        var kerberos = await KerberosSecurityProvider
            .NegotiateAsync(credential, [(kdcHost, kdcPort)], servicePrincipalName, options.LoggerFactory,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Log(options, $"Connecting to {host} as {credential.UserName}@{credential.Realm}.");
        return await ConnectWithSecurityAsync(host,
                new SpnegoSecurityProvider([kerberos], options.LoggerFactory?.CreateLogger<SpnegoSecurityProvider>()),
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to <paramref name="host"/> with Kerberos using the Windows logon session (<see cref="KerberosCredential.FromLogonSession"/>):
    /// Windows gets the service ticket for <paramref name="servicePrincipalName"/> (e.g. <c>host/server01.corp.example.com</c>)
    /// from its own cache and KDC, so neither a password nor a KDC address is needed.
    /// </summary>
    public static async Task<WmiConnection> ConnectAsync(
        string host,
        KerberosCredential credential,
        string servicePrincipalName,
        WmiConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(servicePrincipalName);
        if (!credential.UsesLogonSession)
            throw new ArgumentException("Without a KDC address this overload needs a logon session credential.",
                nameof(credential));

        options ??= new WmiConnectionOptions();
        Log(options, $"Negotiating Kerberos for the logon session against {servicePrincipalName}.");
        var kerberos = await KerberosSecurityProvider
            .NegotiateAsync(credential, [], servicePrincipalName, options.LoggerFactory,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Log(options, $"Connecting to {host} as the logon session.");
        return await ConnectWithSecurityAsync(host,
                new SpnegoSecurityProvider([kerberos], options.LoggerFactory?.CreateLogger<SpnegoSecurityProvider>()),
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects with Kerberos only, for a credential that has no password (<see cref="KerberosCredential.FromTicketCache"/> or
    /// <see cref="KerberosCredential.FromLogonSession"/>) and works out what <paramref name="spnego"/> leaves out, like the
    /// SPNEGO overload does: the SPN from the target's DNS name, and, unless Windows supplies the ticket, the KDC from the
    /// realm's SRV records. NTLM is not offered, since there is no password for it.
    /// </summary>
    public static async Task<WmiConnection> ConnectAsync(
        string host,
        KerberosCredential credential,
        SpnegoOptions spnego,
        WmiConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(spnego);
        options ??= new WmiConnectionOptions();
        spnego = spnego.Copy(options.LoggerFactory);
        var logger = spnego.LoggerFactory?.CreateLogger<SpnegoSecurityProvider>();
        var target = await KerberosDiscovery
            .ResolveAsync(host, credential.Realm, needKdc: !credential.UsesLogonSession, spnego, cancellationToken,
                logger: logger)
            .ConfigureAwait(false);
        if (target is null)
            throw new SecurityProviderException(
                $"Kerberos cannot be set up for {host}: its service principal name or KDC could not be found (see the log).");

        Log(options, $"Negotiating Kerberos for {target.ServicePrincipalName}.");
        var kerberos = await KerberosSecurityProvider
            .NegotiateAsync(credential, target.Kdcs, target.ServicePrincipalName, options.LoggerFactory,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return await ConnectWithSecurityAsync(host, new SpnegoSecurityProvider([kerberos], logger), options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to <paramref name="host"/> negotiating the mechanism with SPNEGO on a single connection: Kerberos when
    /// <paramref name="spnego"/> says where to get a ticket (its optimistic token goes out with the bind), NTLM
    /// otherwise or if the server picks it. <paramref name="credential"/> serves both: its domain is the NTLM domain
    /// (e.g. <c>CONTOSO</c>), <see cref="SpnegoOptions.Realm"/> the Kerberos realm (e.g. <c>AD.CONTOSO.COM</c>).
    /// </summary>
    public static async Task<WmiConnection> ConnectAsync(
        string host,
        Credential credential,
        SpnegoOptions spnego,
        WmiConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(spnego);
        options ??= new WmiConnectionOptions();
        Log(options, $"Connecting to {host} as {credential.Domain}\\{credential.UserName} with SPNEGO.");
        var provider = await SpnegoSecurityProvider
            .CreateAsync(host, credential, spnego.Copy(options.LoggerFactory), cancellationToken)
            .ConfigureAwait(false);
        return await ConnectWithSecurityAsync(host, provider, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shared by every <c>ConnectAsync</c> overload once it has its <see cref="IRpcSecurityProvider"/>.</summary>
    private static async Task<WmiConnection> ConnectWithSecurityAsync(
        string host, IRpcSecurityProvider provider, WmiConnectionOptions options, CancellationToken cancellationToken)
    {
        var security = new RpcSecurity(provider, options.AuthLevel);
        var login = await WbemLevel1LoginClient.ConnectAsync(
                host, security, options.Port, options.KeepAlive, options.KeepAliveInterval,
                options.EffectiveRpcClientOptions, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            Log(options, $"Logging in to {options.Namespace}.");
            var services = await login
                .NtlmLoginAsync(options.Namespace, options.PreferredLocale, null, cancellationToken)
                .ConfigureAwait(false);
            return new WmiConnection(services, host, options.Namespace, options);
        }
        finally
        {
            // The login object itself is not needed once NTLMLogin has handed back an IWbemServices; the
            // namespace connection below lives on its own interface reference.
            await login.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens a namespace below this one (e.g. <c>root</c> to <c>root\cimv2</c>), reusing this connection's
    /// underlying DCOM session rather than logging in again (MS-DCOM lets an object on the object exporter's
    /// own OXID be reached over the same association; <see cref="WmiOrpcConnection.ConnectOrBindAsync"/> is
    /// what notices that and reuses it).
    /// </summary>
    public async Task<WmiConnection> OpenNamespaceAsync(string relativeNamespace,
        CancellationToken cancellationToken = default)
    {
        if (_winRm is not null)
            return new WmiConnection(_winRm.OpenNamespace(relativeNamespace), _host!,
                $@"{_namespacePath}\{relativeNamespace}", Options);

        Log(Options, $"Opening namespace {relativeNamespace}.");
        var result = await Services
            .OpenNamespaceAsync(relativeNamespace, context: WireContext(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var services = result.WorkingNamespace;
        if (result.CallResult is { } callResult)
            await using (callResult)
                services ??= await callResult.GetResultServicesAsync(Options.TimeoutMilliseconds, cancellationToken)
                    .ConfigureAwait(false);

        if (services is null)
            throw new WmiException(unchecked((int)WbemStatus.Failed),
                "OpenNamespace succeeded but returned no namespace.");

        var combined = _namespacePath is null ? relativeNamespace : $@"{_namespacePath}\{relativeNamespace}";
        return new WmiConnection(services, _host, combined, Options) { Context = _context };
    }

    /// <summary>
    /// Creates a <see cref="WmiRefresher"/>: register objects and class enumerations once, then re-read them all
    /// with one call per <see cref="WmiRefresher.RefreshAsync"/>. Meant for repeated polling of performance classes.
    /// </summary>
    public async Task<WmiRefresher> CreateRefresherAsync(CancellationToken cancellationToken = default)
    {
        if (_winRm is not null)
            throw new NotSupportedException("Refreshers need DCOM; they are not available over WinRM.");

        Log(Options, "Creating a refresher.");
        var services = await Services.TryGetRefreshingServicesAsync(cancellationToken).ConfigureAwait(false);
        return new WmiRefresher(this, services);
    }

    /// <summary>
    /// Runs a WQL query and streams the matching instances, fetching them in batches of <see cref="WmiConnectionOptions.BatchSize"/>.
    /// A query that returns classes (<c>SELECT * FROM meta_class</c>, <c>ASSOCIATORS OF {...} WHERE SchemaOnly</c>) gives one
    /// instance per class, with no path and every value null; its <see cref="WmiInstance.Class"/> is the class. Over WinRM such a
    /// class has only its property names; <see cref="EnumerateClassesAsync"/> gives the full definitions.
    /// </summary>
    /// <remarks>Disposing the enumerator early (breaking out of the loop, or cancelling) releases it on the server.</remarks>
    public IAsyncEnumerable<WmiInstance> QueryAsync(
        string query, WbemGenericFlags flags = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_winRm is not null)
            return WithTypedReferenceKeys(_winRm.QueryAsync(query, cancellationToken), cancellationToken);

        var context = WireContext();
        return WithTypedReferenceKeys(RunQueryAsync(
            ct => Services.ExecQueryAsync(query,
                flags | WbemGenericFlags.ReturnImmediately | WbemGenericFlags.ForwardOnly, context, ct),
            $"Query: {query}",
            stopWhenExhausted: true,
            Wrap,
            cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Streams every instance of <paramref name="className"/> (<c>CreateInstanceEnum</c>), including those of its
    /// subclasses unless <paramref name="shallow"/> is set. The same instances as <c>SELECT * FROM className</c> (or, shallow,
    /// <c>... WHERE __CLASS = 'className'</c>) without building a query.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="className"/> is not a CIM class name (letters, digits and underscores, not starting with a digit).</exception>
    public IAsyncEnumerable<WmiInstance> EnumerateInstancesAsync(
        string className, bool shallow = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);
        if (!IsClassName(className))
            throw new ArgumentException($"'{className}' is not a CIM class name.", nameof(className));

        if (_winRm is not null)
            return WithTypedReferenceKeys(_winRm.EnumerateInstancesAsync(className, shallow, cancellationToken),
                cancellationToken);

        var flags = WbemGenericFlags.ReturnImmediately | WbemGenericFlags.ForwardOnly |
                    (shallow ? (WbemGenericFlags)(uint)WbemQueryFlags.Shallow : 0);
        var context = WireContext();
        return WithTypedReferenceKeys(RunQueryAsync(
            ct => Services.CreateInstanceEnumAsync(className, flags, context, ct),
            $"Enumerate instances of {className}",
            stopWhenExhausted: true,
            Wrap,
            cancellationToken), cancellationToken);
    }

    internal static bool IsClassName(string name) =>
        !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// Streams the classes derived from <paramref name="superClass"/> (every class in the namespace when null
    /// or empty), including indirect subclasses unless <paramref name="shallow"/> is set. <paramref name="superClass"/> itself is
    /// not included; with no <paramref name="superClass"/>, shallow gives the classes that have no superclass.
    /// </summary>
    public IAsyncEnumerable<WmiClass> EnumerateClassesAsync(
        string? superClass = null, bool shallow = false, CancellationToken cancellationToken = default)
    {
        if (_winRm is not null)
            return _winRm.EnumerateClassesAsync(superClass, shallow, cancellationToken);

        var flags = WbemGenericFlags.ReturnImmediately | WbemGenericFlags.ForwardOnly |
                    (shallow ? (WbemGenericFlags)(uint)WbemQueryFlags.Shallow : 0);
        var context = WireContext();
        return RunQueryAsync(
            ct => Services.CreateClassEnumAsync(superClass, flags, context, ct),
            $"Enumerate classes of {superClass ?? "(all)"}",
            stopWhenExhausted: true,
            WrapClass,
            cancellationToken);
    }

    /// <summary>
    /// Streams the objects associated with the instance at <paramref name="objectPath"/> (<c>ASSOCIATORS OF</c>): the
    /// instances at the other end of every association that refers to it. Each filter given narrows the result:
    /// <paramref name="associationClass"/> to associations of that class or a subclass, <paramref name="resultClass"/> to
    /// results of that class or a subclass, <paramref name="role"/> to associations that refer to the instance through that
    /// property, and <paramref name="resultRole"/> to results that the association refers to through that property.
    /// </summary>
    /// <remarks>For the other clauses of the WQL statement (<c>ClassDefsOnly</c>, <c>KeysOnly</c>, qualifiers), or for schema queries on a class, run the statement with <see cref="QueryAsync"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="objectPath"/> is not an instance path, or a filter is not a CIM class or property name.</exception>
    public IAsyncEnumerable<WmiInstance> GetAssociatorsAsync(
        string objectPath, string? associationClass = null, string? resultClass = null, string? role = null,
        string? resultRole = null, CancellationToken cancellationToken = default) =>
        QueryAsync(AssociationQuery("ASSOCIATORS", objectPath,
            ("AssocClass", associationClass, nameof(associationClass)),
            ("ResultClass", resultClass, nameof(resultClass)),
            ("Role", role, nameof(role)),
            ("ResultRole", resultRole, nameof(resultRole))), cancellationToken: cancellationToken);

    /// <summary>
    /// Streams the association instances that refer to the instance at <paramref name="objectPath"/> (<c>REFERENCES OF</c>).
    /// <paramref name="resultClass"/> keeps only associations of that class or a subclass, and <paramref name="role"/> only
    /// those that refer to the instance through that property.
    /// </summary>
    /// <remarks>As for <see cref="GetAssociatorsAsync"/>, the rest of the WQL statement is available through <see cref="QueryAsync"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="objectPath"/> is not an instance path, or a filter is not a CIM class or property name.</exception>
    public IAsyncEnumerable<WmiInstance> GetReferencesAsync(
        string objectPath, string? resultClass = null, string? role = null,
        CancellationToken cancellationToken = default) =>
        QueryAsync(AssociationQuery("REFERENCES", objectPath,
            ("ResultClass", resultClass, nameof(resultClass)),
            ("Role", role, nameof(role))), cancellationToken: cancellationToken);

    /// <summary>
    /// <c>ASSOCIATORS OF {path} WHERE ...</c> or <c>REFERENCES OF ...</c>. Both transports take the same statement: the
    /// WinRM listener runs it through WMI's own WQL parser. Filter values are bare identifiers in WQL, so only identifiers
    /// are let through.
    /// </summary>
    internal string AssociationQuery(
        string verb, string objectPath, params (string Keyword, string? Value, string ParameterName)[] filters)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectPath);
        if (!WmiObjectPath.TryParse(ToServerPath(objectPath), out var path) || !path.IsInstance)
            throw new ArgumentException($"'{objectPath}' is not the path of an instance.", nameof(objectPath));

        var query = new StringBuilder($"{verb} OF {{{path.RelativePath}}}");
        var where = false;
        foreach (var (keyword, value, parameterName) in filters)
        {
            if (string.IsNullOrEmpty(value))
                continue;

            if (!IsClassName(value))
                throw new ArgumentException($"'{value}' is not a CIM class or property name.", parameterName);

            query.Append(where ? " " : " WHERE ").Append(keyword).Append(" = ").Append(value);
            where = true;
        }

        return query.ToString();
    }

    /// <summary>
    /// Polls a WQL event query: the one event-delivery mechanism
    /// that needs no inbound RPC, since the client only ever calls out: <c>ExecNotificationQuery</c> plus a
    /// <c>Next</c> loop, MS-WMI 4.3). Unlike <see cref="QueryAsync"/> this never signals "no more results" on
    /// its own (an event subscription has no natural end) — the server returns a WBEM_S_FALSE status for a
    /// <c>Next</c> call that simply timed out waiting for the next event, not one that ran out of events —
    /// so the stream runs until the caller disposes it or cancels, polling again after every empty wait.
    /// </summary>
    public IAsyncEnumerable<WmiInstance> WatchAsync(
        string eventQuery, WbemGenericFlags flags = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventQuery);
        if (_winRm is not null)
            return WithTypedReferenceKeys(_winRm.WatchAsync(eventQuery, cancellationToken), cancellationToken);

        var context = WireContext();
        return WithTypedReferenceKeys(RunQueryAsync(
            ct => Services.ExecNotificationQueryAsync(eventQuery,
                flags | WbemGenericFlags.ReturnImmediately | WbemGenericFlags.ForwardOnly, context, ct),
            $"Watch: {eventQuery}",
            stopWhenExhausted: false,
            Wrap,
            cancellationToken,
            batchSize: 1), cancellationToken); // events trickle in; a larger batch makes the server hold the first one until the batch fills or times out
    }

    private async IAsyncEnumerable<T> RunQueryAsync<T>(
        Func<CancellationToken, Task<EnumWbemClassObjectClient>> execute,
        string logMessage,
        bool stopWhenExhausted,
        Func<WmioObjectBlock, T> wrap,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        uint? batchSize = null)
    {
        var requested = batchSize ?? Options.BatchSize;
        Log(Options, logMessage);
        var enumerator = await execute(cancellationToken).ConfigureAwait(false);
        await using (enumerator.ConfigureAwait(false))
        {
            var smart = Options.UseSmartEnumeration
                ? await enumerator.TryGetSmartEnumAsync(cancellationToken).ConfigureAwait(false)
                : null;
            try
            {
                var hasMore = true;
                while (hasMore || !stopWhenExhausted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = smart is not null
                        ? await smart.NextAsync(requested, Options.TimeoutMilliseconds, cancellationToken)
                            .ConfigureAwait(false)
                        : await enumerator.NextAsync(requested, Options.TimeoutMilliseconds, cancellationToken)
                            .ConfigureAwait(false);
                    hasMore = batch.HasMore;
                    foreach (var block in batch.Objects)
                        if (block is not null)
                            yield return wrap(block);
                }
            }
            finally
            {
                if (smart is not null)
                    await smart.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Retrieves a CIM instance by its path: relative to this connection's namespace, or qualified with it
    /// (<c>\\server\root\cimv2:Win32_Process.Handle="4"</c>, like <see cref="WmiSystemProperties.Path"/>). A singleton's path
    /// ends in <c>=@</c> (<c>Win32_WmiSetting=@</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="objectPath"/> is the path of a class; use <see cref="GetClassAsync"/>.</exception>
    /// <exception cref="WmiException">There is no such instance (<c>WBEM_E_NOT_FOUND</c>), or another failure.</exception>
    public async Task<WmiInstance> GetInstanceAsync(string objectPath, WbemGenericFlags flags = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectPath);
        var serverPath = ToServerPath(objectPath);
        if (WmiObjectPath.TryParse(serverPath, out var parsed) && parsed.IsClass)
            throw new ArgumentException($"'{objectPath}' is the path of a class; get it with GetClassAsync.",
                nameof(objectPath));

        WmiInstance instance;
        if (_winRm is not null)
            instance = await _winRm.GetInstanceAsync(serverPath, cancellationToken).ConfigureAwait(false);
        else
        {
            var block = await GetObjectBlockAsync(serverPath, flags, cancellationToken).ConfigureAwait(false);
            instance = block is null
                ? throw new WmiException(unchecked((int)WbemStatus.NotFound),
                    $"GetObject returned no object for {objectPath}.")
                : Wrap(block);
        }

        return (await TypeReferenceKeysAsync(instance, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Retrieves a CIM class's definition: its properties, methods and qualifiers.</summary>
    /// <param name="className">The class name.</param>
    /// <param name="flags"><see cref="WbemGenericFlags.UseAmendedQualifiers"/> includes the localized qualifiers (<c>Description</c>, ...). Not used over WinRM.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentException"><paramref name="className"/> is not a CIM class name.</exception>
    /// <exception cref="WmiException">There is no such class (<c>WBEM_E_NOT_FOUND</c>), or another failure.</exception>
    public async Task<WmiClass> GetClassAsync(string className, WbemGenericFlags flags = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);
        if (!IsClassName(className))
            throw new ArgumentException($"'{className}' is not a CIM class name.", nameof(className));

        if (_winRm is not null)
            return await _winRm.GetClassAsync(className, cancellationToken).ConfigureAwait(false);

        var block = await GetObjectBlockAsync(className, flags, cancellationToken).ConfigureAwait(false);
        return block is { IsClass: true }
            ? WrapClass(block)
            : throw new WmiException(unchecked((int)WbemStatus.NotFound),
                $"GetObject returned no class for {className}.");
    }

    /// <summary>GetObject, waiting on the call result when the server answers with one instead of the object, and always releasing it.</summary>
    private async Task<WmioObjectBlock?> GetObjectBlockAsync(string objectPath, WbemGenericFlags flags,
        CancellationToken cancellationToken)
    {
        var result = await Services.GetObjectAsync(objectPath, flags, WireContext(), cancellationToken)
            .ConfigureAwait(false);
        if (result.CallResult is not { } callResult)
            return result.Object;

        await using (callResult)
            return result.Object
                   ?? await callResult.GetResultObjectAsync(Options.TimeoutMilliseconds, cancellationToken)
                       .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or updates a CIM instance (<c>PutInstance</c>). The server answers this semisynchronously
    /// (via a non-null <c>ppCallResult</c>, even without <see cref="WbemGenericFlags.ReturnImmediately"/>): the
    /// initial reply's own status is just "request accepted," not the operation's real outcome, so this polls
    /// <see cref="WbemCallResultClient.GetCallStatusAsync"/> for that; skipping it silently swallowed a real
    /// failure (e.g. WBEM_E_INVALID_CLASS) that only showed up later as the created instance not existing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Over DCOM: <paramref name="instance"/> has no binary class definition (it was built on the client, or read over WinRM).</exception>
    public async Task PutInstanceAsync(
        WmiInstance instance, WbemChangeFlags changeFlags = WbemChangeFlags.CreateOrUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (_winRm is not null)
        {
            await _winRm.PutInstanceAsync(instance, changeFlags, cancellationToken).ConfigureAwait(false);
            return;
        }

        var callResult = await Services
            .PutInstanceAsync(instance.ToInstanceBlock(), (WbemGenericFlags)(uint)changeFlags, WireContext(),
                cancellationToken)
            .ConfigureAwait(false);
        await CompleteSemisynchronousCallAsync(callResult, "PutInstance", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the CIM instance at <paramref name="objectPath"/>. See the remarks on <see cref="PutInstanceAsync"/>.</summary>
    public async Task DeleteInstanceAsync(string objectPath, CancellationToken cancellationToken = default)
    {
        if (_winRm is not null)
        {
            await _winRm.DeleteInstanceAsync(ToServerPath(objectPath), cancellationToken).ConfigureAwait(false);
            return;
        }

        var callResult = await Services
            .DeleteInstanceAsync(ToServerPath(objectPath), context: WireContext(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await CompleteSemisynchronousCallAsync(callResult, "DeleteInstance", cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteSemisynchronousCallAsync(
        WbemCallResultClient? callResult, string methodName, CancellationToken cancellationToken)
    {
        if (callResult is null)
            return;

        await using (callResult.ConfigureAwait(false))
        {
            int status;
            do
                status = await callResult.GetCallStatusAsync(PollStepMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            while (status == (int)WbemStatus.Timedout);

            WmiStatus.ThrowIfError(status, $"{methodName} failed");
        }
    }

    /// <summary>
    /// How long one wait on a running call may last on the server: <see cref="WmiConnectionOptions.Timeout"/>, but short enough to
    /// come back within the RPC timeout, so a slow operation never outlasts any single call.
    /// </summary>
    private int PollStepMilliseconds
    {
        get
        {
            var wait = Options.TimeoutMilliseconds == WbemTimeout.NoWait ? 1000 : Options.TimeoutMilliseconds;
            var rpcTimeout = Options.EffectiveRpcTimeout;
            if (rpcTimeout == Timeout.InfiniteTimeSpan)
                return wait;

            var half = (int)Math.Min(rpcTimeout.TotalMilliseconds / 2, int.MaxValue);
            return wait == WbemTimeout.Infinite ? half : Math.Min(wait, half);
        }
    }

    /// <summary>
    /// Calls a method on the CIM class or instance at <paramref name="objectPath"/> (a bare class name for a
    /// static method, e.g. <c>Win32_Process</c> for <c>Create</c>, or an instance path for an instance
    /// method, e.g. <c>Win32_Process.Handle="1234"</c> for <c>Terminate</c>).
    /// </summary>
    /// <remarks>
    /// When <paramref name="parameters"/> is non-empty, this first fetches the method's declared input
    /// signature (the class behind <paramref name="objectPath"/>'s own method definition: an instance path's
    /// embedded class carries no method metadata, MS-WMIO 2.2.53, so the bare class name is looked up
    /// separately) and builds <c>__PARAMETERS</c> from <em>every</em> declared input property, not just the
    /// ones <paramref name="parameters"/> names. A provider such as <c>Win32_Process.Create</c>
    /// rejects a <c>__PARAMETERS</c> object missing one of its other declared properties, even as an explicit
    /// null, with ReturnValue 21 (invalid parameter) — it is not enough to send only the properties the caller
    /// cares about. CIM types for the supplied values are inferred from the .NET values given only when a
    /// method's signature cannot be found (e.g. it declares no input parameters at all).
    /// <para>
    /// A parameter may be a scalar, an array or any other sequence (<c>string[]</c>, <c>byte[]</c>, <c>List&lt;int&gt;</c>),
    /// or an embedded object: a <see cref="WmiInstance"/>, such as an instance of <c>Win32_ProcessStartup</c> for
    /// <c>Win32_Process.Create</c>'s <c>ProcessStartupInformation</c>. Make one from its class
    /// (<see cref="WmiInstance(WmiClass)"/> with <see cref="GetClassAsync"/>), set its properties, and pass it.
    /// </para>
    /// </remarks>
    public async Task<WmiInstance?> InvokeMethodAsync(
        string objectPath,
        string methodName,
        IReadOnlyDictionary<string, object?>? parameters = null,
        WbemGenericFlags flags = 0,
        CancellationToken cancellationToken = default)
    {
        Log(Options, $"Invoking {methodName} on {objectPath}.");
        objectPath = ToServerPath(objectPath);
        if (_winRm is not null)
            return await TypeReferenceKeysAsync(
                await _winRm.InvokeMethodAsync(objectPath, methodName, parameters, cancellationToken)
                    .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        WmioObjectBlock? inParams = null;
        if (parameters is { Count: > 0 })
        {
            var signature = await FindInputSignatureAsync(objectPath, methodName, cancellationToken)
                .ConfigureAwait(false);
            inParams = BuildParametersBlock(parameters, signature);
        }

        // Always semisynchronous, so a method can run for as long as it needs: the wait below comes back in steps, where one
        // synchronous call would have to finish within the RPC timeout.
        var result = await Services
            .ExecMethodAsync(objectPath, methodName, inParams, flags | WbemGenericFlags.ReturnImmediately,
                WireContext(), cancellationToken)
            .ConfigureAwait(false);

        if (result.CallResult is { } callResult)
            await using (callResult.ConfigureAwait(false))
                while (true)
                    try
                    {
                        var outParams = await callResult.GetResultObjectAsync(PollStepMilliseconds, cancellationToken)
                            .ConfigureAwait(false);
                        return await TypeReferenceKeysAsync(outParams is null ? null : Wrap(outParams),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (WmiException ex) when (ex.HResult == (int)WbemStatus.Timedout)
                    {
                        // Still running: wait again.
                    }

        return await TypeReferenceKeysAsync(result.OutParameters is null ? null : Wrap(result.OutParameters),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The path the server takes. A server only resolves paths relative to the namespace it is connected to, so a path
    /// qualified with a machine and namespace is cut down to its relative part. The machine is not checked: the connection
    /// is to one host, and the name a path carries for it (an address, a short name, a DNS name, ".") varies.
    /// </summary>
    internal string ToServerPath(string objectPath)
    {
        ArgumentNullException.ThrowIfNull(objectPath);
        if (!WmiObjectPath.TryParse(objectPath, out var path) || path.Namespace is null)
            return objectPath;

        if (_namespacePath is not null &&
            !string.Equals(path.Namespace, _namespacePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"The path is in the namespace '{path.Namespace}' but this connection is to '{_namespacePath}'; open that namespace with OpenNamespaceAsync.",
                nameof(objectPath));

        return path.RelativePath;
    }

    private async IAsyncEnumerable<WmiInstance> WithTypedReferenceKeys(
        IAsyncEnumerable<WmiInstance> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var instance in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return (await TypeReferenceKeysAsync(instance, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Gives the key properties of each reference in <paramref name="instance"/> (embedded objects included) the types the
    /// referenced class declares. A path, or a WinRM selector, only tells a string from a number or a boolean, and the two
    /// transports tell it differently; the class is fetched once per connection. A class in another namespace, or one the
    /// server does not give, leaves the keys as read.
    /// </summary>
    private async Task<WmiInstance?> TypeReferenceKeysAsync(WmiInstance? instance, CancellationToken cancellationToken)
    {
        if (instance is null)
            return null;

        foreach (var reference in ReferencesIn(instance))
        {
            if (await KeyTypesAsync(reference, cancellationToken).ConfigureAwait(false) is not { } keyTypes)
                continue;

            foreach (var property in reference.Properties)
                if (property.Value is { } value && keyTypes.TryGetValue(property.Name, out var type) &&
                    type != property.Type && WmiValues.TryToKeyType(value, type, out var converted))
                    property.Retype(type, converted);
        }

        return instance;
    }

    private static IEnumerable<WmiInstance> ReferencesIn(WmiInstance instance)
    {
        foreach (var property in instance.Properties)
            foreach (var reference in ReferencesIn(property.Value))
                yield return reference;
    }

    private static IEnumerable<WmiInstance> ReferencesIn(object? value)
    {
        switch (value)
        {
            case WmiInstance { IsReference: true } reference:
                yield return reference;
                break;
            case WmiInstance embedded:
                foreach (var reference in ReferencesIn(embedded))
                    yield return reference;
                break;
            case object?[] or WmiInstance[]:
                foreach (var element in (IEnumerable)value)
                    foreach (var reference in ReferencesIn(element))
                        yield return reference;
                break;
        }
    }

    private readonly ConcurrentDictionary<string, Task<Dictionary<string, WmiType>?>> _keyTypes =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, WmiType>?> KeyTypesAsync(WmiInstance reference,
        CancellationToken cancellationToken)
    {
        var system = reference.SystemProperties;
        if (system.Namespace is { } ns && !string.Equals(ns, _namespacePath, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!IsClassName(system.ClassName))
            return null;

        var fetch = _keyTypes.GetOrAdd(system.ClassName, FetchKeyTypesAsync);
        try
        {
            return await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (fetch.IsFaulted || fetch.IsCanceled)
        {
            // A transport failure says nothing about the class: try again next time.
            _keyTypes.TryRemove(new KeyValuePair<string, Task<Dictionary<string, WmiType>?>>(system.ClassName, fetch));
            throw;
        }
    }

    private async Task<Dictionary<string, WmiType>?> FetchKeyTypesAsync(string className)
    {
        try
        {
            var @class = await GetClassAsync(className).ConfigureAwait(false);
            return @class.Properties
                .Where(p => (p.Flags & WmiFlags.Key) != 0 && p.Type != WmiType.Unknown)
                .ToDictionary(p => p.Name, p => p.Type, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is WmiException or WsmanException)
        {
            // Keys as read are better than failing the read.
            return null;
        }
    }


    internal WmiInstance Wrap(WmioObjectBlock block) => WmiInstance.FromWire(block, _host, _namespacePath);

    private WmiClass WrapClass(WmioObjectBlock block) =>
        block.Class is { } @class
            ? WmiClass.FromWire(@class, block.Decoration?.Server ?? _host,
                block.Decoration?.Namespace ?? _namespacePath)
            : throw new WmiException(unchecked((int)WbemStatus.Failed),
                "The server returned an instance where a class was expected.");

    /// <summary>
    /// Looks up <paramref name="methodName"/>'s declared input-parameter class on the class behind
    /// <paramref name="objectPath"/> (its bare class name, stripped of any instance-key suffix), or null when
    /// the class, the method, or a declared input signature cannot be found.
    /// </summary>
    private async Task<WmioClass?> FindInputSignatureAsync(string objectPath, string methodName,
        CancellationToken cancellationToken)
    {
        var className = WmiObjectPath.TryParse(objectPath, out var parsed)
            ? parsed.ClassName
            : objectPath.Split('.', 2)[0];
        var classBlock = await GetObjectBlockAsync(className, 0, cancellationToken).ConfigureAwait(false);
        var method = classBlock?.Class?.Methods.FirstOrDefault(m => m.Name == methodName);
        return method?.DecodeInputSignature();
    }

    /// <summary>
    /// Builds a <c>__PARAMETERS</c> class (MS-WMIO 2.3.3) for an <c>ExecMethod</c> call. When
    /// <paramref name="signature"/> is given (the method's own declared input-parameter class, from
    /// <see cref="FindInputSignatureAsync"/>), every one of its properties is kept, with <paramref name="parameters"/>'
    /// values substituted in by name; without it, a synthetic class is built from only the properties
    /// <paramref name="parameters"/> names, inferring each one's CIM type. Internal (not private) so its own
    /// byte-level result can be checked directly, without a live connection.
    /// </summary>
    /// <remarks>
    /// A server sends a method's input parameters as a <em>class</em> whose property values are the
    /// class's own defaults, not an instance of <c>__PARAMETERS</c>: <see cref="WmioObjectBlock.ForInstance"/> here, matching the IDL's
    /// <c>IWbemClassObject*</c> wording literally, is what a provider rejects every parameter
    /// value of (<c>Win32_Process.Create</c> returning ReturnValue 21, invalid parameter, for every call).
    /// Sending only the caller-named properties, even once the class-vs-instance shape was fixed, still got
    /// ReturnValue 21: a provider expects every declared input property to be present (even as an
    /// explicit null), not just the ones actually supplied.
    /// </remarks>
    internal static WmioObjectBlock BuildParametersBlock(IReadOnlyDictionary<string, object?> parameters,
        WmioClass? signature = null)
    {
        if (signature is not null)
        {
            var unknown = parameters.Keys.Where(name => signature.FindProperty(name) is null).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException(
                    $"'{signature.Name}' has no input parameter named: {string.Join(", ", unknown)}.",
                    nameof(parameters));

            // Parameter names match the signature's case-insensitively, as everywhere in CIM.
            parameters = signature.ByPropertyName(parameters, nameof(parameters));

            var merged = signature.Properties
                .Select(p => parameters.TryGetValue(p.Name, out var value)
                    ? p with
                    {
                        DefaultValue = CoerceValue(value, p.Type), NdFlag = new NullAndDefaultFlag(value is null, false)
                    }
                    : p)
                .ToList();
            return WmioObjectBlock.ForClass(signature with { Properties = merged });
        }

        ushort order = 0;
        var properties = parameters
            .Select(kv =>
            {
                var type = InferCimType(kv.Value);
                var value = type.IsArray() || type is WbemType.Object or WbemType.DateTime or WbemType.Reference
                    ? CoerceValue(kv.Value, type)
                    : kv.Value;
                var id = order++;
                IReadOnlyList<WmioQualifier> qualifiers =
                [
                    new("ID", QualifierFlavor.PropagateToInstance | QualifierFlavor.NotOverridable,
                        WbemType.Sint32, (int)id),
                    new("In", QualifierFlavor.None, WbemType.Boolean, true),
                    new("CIMTYPE", QualifierFlavor.PropagateToInstance | QualifierFlavor.PropagateToDerivedClass,
                        WbemType.String, MofTypeName(type.BaseType(), kv.Value)),
                ];
                return new WmioProperty(kv.Key, type, false, id, 0, qualifiers,
                    new NullAndDefaultFlag(value is null, false),
                    value, 0);
            })
            .ToList();

        var @class = new WmioClass(
            "__PARAMETERS", [], [new WmioQualifier("abstract", QualifierFlavor.None, WbemType.Boolean, true)],
            properties, []);

        return WmioObjectBlock.ForClass(@class);
    }

    /// <summary>
    /// The MOF type name a server puts in a method parameter's CIMTYPE qualifier: e.g. <c>"string"</c>, <c>"sint32"</c>. <see cref="BuildParametersBlock"/>
    /// only ever infers the base types listed here (an array's CIMTYPE names its element type), never <see cref="WbemType.Object"/>.
    /// </summary>
    private static string MofTypeName(WbemType type, object? value = null) => type switch
    {
        WbemType.Object => "object:" + EmbeddedClassName(value),
        WbemType.Reference => "ref:" + ReferencedClassName(value),
        WbemType.String => "string",
        WbemType.Boolean => "boolean",
        WbemType.Sint8 => "sint8",
        WbemType.Uint8 => "uint8",
        WbemType.Sint16 => "sint16",
        WbemType.Uint16 => "uint16",
        WbemType.Sint32 => "sint32",
        WbemType.Uint32 => "uint32",
        WbemType.Sint64 => "sint64",
        WbemType.Uint64 => "uint64",
        WbemType.Real32 => "real32",
        WbemType.Real64 => "real64",
        WbemType.DateTime => "datetime",
        _ => throw new NotSupportedException($"No MOF type name for {type}."),
    };

    /// <summary>The class of an embedded-object parameter (or of the first object in an array of them), for its <c>object:Class</c> CIMTYPE.</summary>
    private static string EmbeddedClassName(object? value) => value switch
    {
        WmiInstance embedded => embedded.SystemProperties.ClassName,
        IEnumerable sequence => sequence.OfType<WmiInstance>().FirstOrDefault()?.SystemProperties.ClassName
                                ?? throw new NotSupportedException(
                                    "An array of embedded objects needs at least one element to name its class."),
        _ => throw new NotSupportedException("An embedded-object parameter needs a WmiInstance."),
    };

    /// <summary>The class a reference parameter (or the first of an array of them) refers to, for its <c>ref:Class</c> CIMTYPE.</summary>
    private static string ReferencedClassName(object? value) => value switch
    {
        WmiInstance reference => reference.SystemProperties.ClassName,
        IEnumerable sequence => sequence.OfType<WmiInstance>().First().SystemProperties.ClassName,
        _ => throw new NotSupportedException("A reference parameter needs a WmiInstance."),
    };

    /// <summary>
    /// The CIM type to send <paramref name="value"/> as when a method's signature is not known: a scalar for the common
    /// .NET types, the same with <see cref="WbemType.ArrayFlag"/> for an array or any other sequence of them (<c>string[]</c>,
    /// <c>byte[]</c>, <c>List&lt;int&gt;</c>, ...). A sequence typed as <c>object</c> is typed by its first non-null element.
    /// </summary>
    internal static WbemType InferCimType(object? value)
    {
        if (value is null or string)
            return WbemType.String;

        if (value is WmiInstance { IsReference: true })
            return WbemType.Reference;

        if (value is IEnumerable items &&
            items.Cast<object?>().FirstOrDefault(e => e is not null) is WmiInstance { IsReference: true })
            return WbemType.Reference.AsArray();

        return value is IEnumerable sequence ? InferElementType(sequence).AsArray() : InferScalarType(value.GetType());
    }

    private static WbemType InferElementType(IEnumerable sequence)
    {
        var elementType = sequence is Array array
            ? array.GetType().GetElementType()
            : sequence.GetType().GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        if (elementType is null || elementType == typeof(object))
        {
            var first = sequence.Cast<object?>().FirstOrDefault(e => e is not null)
                        ?? throw new NotSupportedException(
                            "Cannot infer the CIM type of an empty or all-null untyped sequence; pass a typed array such as string[].");
            elementType = first.GetType();
        }

        if (elementType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(elementType))
            throw new NotSupportedException("A CIM array cannot hold arrays.");

        return InferScalarType(elementType);
    }

    private static WbemType InferScalarType(Type type) =>
        type == typeof(string) ? WbemType.String
        : type == typeof(bool) ? WbemType.Boolean
        : type == typeof(sbyte) ? WbemType.Sint8
        : type == typeof(byte) ? WbemType.Uint8
        : type == typeof(short) ? WbemType.Sint16
        : type == typeof(ushort) ? WbemType.Uint16
        : type == typeof(int) ? WbemType.Sint32
        : type == typeof(uint) ? WbemType.Uint32
        : type == typeof(long) ? WbemType.Sint64
        : type == typeof(ulong) ? WbemType.Uint64
        : type == typeof(float) ? WbemType.Real32
        : type == typeof(double) ? WbemType.Real64
        : type == typeof(char) ? WbemType.Char16
        : type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ? WbemType.DateTime
        : type == typeof(WmiInstance) ? WbemType.Object
        : throw new NotSupportedException($"Cannot infer a CIM type for a method parameter value of type {type}.");

    /// <summary>
    /// Converts a caller-supplied parameter value to the exact CLR type its signature's declared
    /// <paramref name="type"/> requires for inline encoding (<see cref="WmioValue.WriteInline"/> rejects
    /// anything else, e.g. a boxed <see cref="int"/> for a <see cref="WbemType.Uint32"/> property):
    /// for example <c>Win32_Process.Terminate</c>'s <c>Reason</c> parameter, declared <c>uint32</c>,
    /// which a caller naturally supplies as a plain <see langword="int"/> literal.
    /// </summary>
    internal static object? CoerceValue(object? value, WbemType type)
    {
        if (value is null)
            return null;

        if (type.IsArray())
            return CoerceArray(value, type.BaseType());

        return type.BaseType() switch
        {
            WbemType.Sint8 => Convert.ToSByte(value, CultureInfo.InvariantCulture),
            WbemType.Uint8 => Convert.ToByte(value, CultureInfo.InvariantCulture),
            WbemType.Sint16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            WbemType.Uint16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            WbemType.Sint32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            WbemType.Uint32 => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            WbemType.Sint64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            WbemType.Uint64 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            WbemType.Real32 => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            WbemType.Real64 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            WbemType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            WbemType.Char16 => Convert.ToChar(value, CultureInfo.InvariantCulture),
            WbemType.DateTime => WmiValues.ToDmtf(value) ?? Convert.ToString(value, CultureInfo.InvariantCulture),
            WbemType.Reference => value is WmiInstance reference
                ? WmiValues.ReferencePath(reference)
                : Convert.ToString(value, CultureInfo.InvariantCulture),
            WbemType.String => Convert.ToString(value, CultureInfo.InvariantCulture),
            WbemType.Object => value switch
            {
                WmiInstance embedded => embedded.ToInstanceBlock(),
                WmioObjectBlock block => block,
                _ => throw new ArgumentException(
                    $"A value declared as an embedded object needs a WmiInstance (make one from a class from GetClassAsync), not {value.GetType()}."),
            },
            _ => value,
        };
    }

    /// <summary>
    /// An array parameter's value as the <c>object?[]</c> of exactly-typed elements the WMIO encoder takes: any sequence
    /// (<c>string[]</c>, <c>byte[]</c>, <c>List&lt;int&gt;</c>, ...), each element coerced like a scalar of the declared
    /// element type.
    /// </summary>
    private static object?[] CoerceArray(object value, WbemType elementType)
    {
        if (value is string || value is not IEnumerable sequence)
            throw new ArgumentException(
                $"A parameter declared as an array of {elementType} needs an array or other sequence, not {value.GetType()}.");

        return [.. sequence.Cast<object?>().Select(element => CoerceValue(element, elementType))];
    }

    private static void Log(WmiConnectionOptions options, string message) =>
        options.LoggerFactory?.CreateLogger<WmiConnection>().LogDebug("{Message}", message);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _winRm?.DisposeAsync() ?? Services.DisposeAsync();
}
