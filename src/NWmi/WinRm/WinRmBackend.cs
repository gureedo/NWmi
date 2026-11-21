using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using NWmi.Client;
using NWmi.Wmi;

namespace NWmi.WinRm;

/// <summary>
/// What <see cref="WmiConnection"/> runs on when it is connected over WinRM instead of DCOM: the same operations
/// as WS-Management requests against the Windows WMI provider (Get/Put/Create/Delete for instances, Enumerate and
/// Pull for queries, Invoke for methods, a pull subscription for events).
/// </summary>
internal sealed class WinRmBackend : IAsyncDisposable
{
    private readonly WsmanClient _client;
    private readonly string _host;
    private readonly string _namespacePath;
    private readonly uint _batchSize;
    private readonly Lease _lease;
    private int _disposed;

    private readonly ConcurrentDictionary<string, Task<WsmanClassSchema?>> _schemas =
        new(StringComparer.OrdinalIgnoreCase);

    private WinRmBackend(WsmanClient client, string host, string namespacePath, uint batchSize, Lease lease)
    {
        _client = client;
        _host = host;
        _namespacePath = namespacePath;
        _batchSize = batchSize;
        _lease = lease;
    }

    public static async Task<WinRmBackend> ConnectAsync(
        WsmanTransport transport, string host, string namespacePath, TimeSpan timeout, uint batchSize,
        CancellationToken cancellationToken)
    {
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new WinRmBackend(new WsmanClient(transport, namespacePath, timeout), host, namespacePath, batchSize,
            new Lease(transport));
    }

    public WinRmBackend OpenNamespace(string relativeNamespace)
    {
        var combined = $@"{_namespacePath}\{relativeNamespace}";
        _lease.Acquire();
        return new WinRmBackend(new WsmanClient(_lease.Transport, combined, ClientTimeout), _host, combined,
            _batchSize,
            _lease);
    }

    private TimeSpan ClientTimeout => _client.Timeout;

    public async Task<WmiObject?> GetObjectAsync(string objectPath, CancellationToken cancellationToken)
    {
        var path = Parse(objectPath);
        if (path.IsClass)
        {
            // WS-Man has no "get class"; a meta_class query returns the class with all values null.
            var query =
                $"SELECT * FROM meta_class WHERE __CLASS = '{path.ClassName.Replace("'", "''", StringComparison.Ordinal)}'";
            await foreach (var cls in EnumerateAsync(query, cancellationToken).ConfigureAwait(false))
                return cls;

            throw new WmiException(unchecked((int)WbemStatus.InvalidClass),
                $"The class {path.ClassName} does not exist");
        }

        var body = await _client
            .CallAsync(_client.ResourceUri(path.ClassName), Wsman.ActionGet, Selectors(path), null, cancellationToken)
            .ConfigureAwait(false);
        var instance = body.Elements().FirstOrDefault() ?? throw new WsmanException("The Get reply holds no instance.");
        var schemas = await SchemasForAsync(instance, path.ClassName).ConfigureAwait(false);
        return WsmanObjectReader.Read(instance, null, [.. path.Keys.Select(k => k.Key)], _host, _namespacePath,
            path.ClassName, schemas);
    }

    public IAsyncEnumerable<WmiObject> QueryAsync(string query, CancellationToken cancellationToken) =>
        EnumerateAsync(query, cancellationToken);

    /// <summary>WS-Management has no class-enumeration call that takes a depth, so this is the equivalent WQL; <paramref name="className"/> is already checked to be a plain identifier.</summary>
    public IAsyncEnumerable<WmiObject> EnumerateInstancesAsync(
        string className, bool shallow, CancellationToken cancellationToken) =>
        EnumerateAsync(
            shallow ? $"SELECT * FROM {className} WHERE __CLASS = '{className}'" : $"SELECT * FROM {className}",
            cancellationToken);

    public IAsyncEnumerable<WmiObject> EnumerateClassesAsync(
        string? superClass, bool shallow, CancellationToken cancellationToken)
    {
        var escaped = superClass?.Replace("'", "''", StringComparison.Ordinal);
        var query = string.IsNullOrEmpty(superClass)
            ? "SELECT * FROM meta_class"
            : shallow
                ? $"SELECT * FROM meta_class WHERE __SUPERCLASS = '{escaped}'"
                : $"SELECT * FROM meta_class WHERE __this ISA '{escaped}'";
        return EnumerateAsync(query, cancellationToken, skipClass: shallow ? null : superClass);
    }

    private async IAsyncEnumerable<WmiObject> EnumerateAsync(
        string query, [EnumeratorCancellation] CancellationToken cancellationToken, string? skipClass = null)
    {
        var uri = _client.ResourceUri("*");
        var enumerate = new XElement(Wsman.Enumeration + "Enumerate",
            new XElement(Wsman.Man + "OptimizeEnumeration"),
            new XElement(Wsman.Man + "MaxElements", _batchSize),
            new XElement(Wsman.Man + "Filter", new XAttribute("Dialect", Wsman.WqlDialect), query),
            new XElement(Wsman.Man + "EnumerationMode", "EnumerateObjectAndEPR"));

        var body = await _client.CallAsync(uri, Wsman.ActionEnumerate, null, enumerate, cancellationToken)
            .ConfigureAwait(false);
        var response = body.Element(Wsman.Enumeration + "EnumerateResponse")
                       ?? throw new WsmanException("The Enumerate reply has no EnumerateResponse.");
        var finished = false;
        string? context = null;
        try
        {
            while (true)
            {
                context = response.Element(Wsman.Enumeration + "EnumerationContext")?.Value.Trim();
                finished = Child(response, "EndOfSequence") is not null || string.IsNullOrEmpty(context);
                foreach (var item in Child(response, "Items")?.Elements() ?? [])
                {
                    var (instance, epr) = SplitItem(item);
                    var queryClass = WsmanObjectReader.ClassOfQuery(query);
                    // Class definitions (meta_class) hold no values worth typing, and there can be thousands of them.
                    var schemas = query.Contains("meta_class", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : await SchemasForAsync(instance, queryClass).ConfigureAwait(false);
                    var obj = WsmanObjectReader.Read(instance, epr, null, _host, _namespacePath, queryClass, schemas);
                    if (skipClass is null ||
                        !string.Equals(obj.ClassName, skipClass, StringComparison.OrdinalIgnoreCase))
                        yield return obj;
                }

                if (finished)
                    yield break;

                cancellationToken.ThrowIfCancellationRequested();
                var pull = new XElement(Wsman.Enumeration + "Pull",
                    new XElement(Wsman.Enumeration + "EnumerationContext", context),
                    new XElement(Wsman.Enumeration + "MaxElements", _batchSize));
                var next = await _client.CallAsync(uri, Wsman.ActionPull, null, pull, cancellationToken)
                    .ConfigureAwait(false);
                response = next.Element(Wsman.Enumeration + "PullResponse")
                           ?? throw new WsmanException("The Pull reply has no PullResponse.");
            }
        }
        finally
        {
            if (!finished && !string.IsNullOrEmpty(context))
                await ReleaseAsync(uri, context).ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync(string uri, string context)
    {
        try
        {
            await _client.CallAsync(uri, Wsman.ActionRelease, null,
                new XElement(Wsman.Enumeration + "Release",
                    new XElement(Wsman.Enumeration + "EnumerationContext", context)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBestEffortFailure(ex))
        {
            // Releasing is courtesy; the server drops the context when it times out.
        }
    }

    /// <summary>EnumerateResponse puts Items in the WS-Man namespace and PullResponse in the enumeration one.</summary>
    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static (XElement Instance, XElement? Epr) SplitItem(XElement item)
    {
        // EnumerateObjectAndEPR wraps each result in w:Item next to its endpoint reference; plain results are bare.
        if (item.Name == Wsman.Man + "Item")
        {
            var epr = item.Elements().FirstOrDefault(e => e.Name == Wsman.Addressing + "EndpointReference");
            var instance = item.Elements().First(e => e != epr);
            return (instance, epr);
        }

        return (item, null);
    }

    /// <summary>How long one event pull waits. The listener holds a pull for its whole timeout even when an event is ready, so keep it short.</summary>
    private static readonly TimeSpan EventPullWait = TimeSpan.FromSeconds(3);

    public async IAsyncEnumerable<WmiObject> WatchAsync(
        string eventQuery, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var uri = _client.ResourceUri("*");
        var subscribe = new XElement(Wsman.Eventing + "Subscribe",
            new XElement(Wsman.Eventing + "Delivery",
                new XAttribute("Mode", "http://schemas.dmtf.org/wbem/wsman/1/wsman/Pull")),
            new XElement(Wsman.Man + "Filter", new XAttribute("Dialect", Wsman.WqlDialect), eventQuery));
        var body = await _client.CallAsync(uri, Wsman.ActionSubscribe, null, subscribe, cancellationToken)
            .ConfigureAwait(false);
        var response = body.Element(Wsman.Eventing + "SubscribeResponse")
                       ?? throw new WsmanException("The Subscribe reply has no SubscribeResponse.");
        var context = response.Descendants(Wsman.Enumeration + "EnumerationContext").FirstOrDefault()?.Value.Trim()
                      ?? throw new WsmanException("The Subscribe reply names no enumeration context.");
        var subscription = SubscriptionHeaders(response);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pull = new XElement(Wsman.Enumeration + "Pull",
                    new XElement(Wsman.Enumeration + "EnumerationContext", context),
                    new XElement(Wsman.Enumeration + "MaxElements", 1));
                XElement? pulled = null;
                try
                {
                    pulled = (await _client
                        .CallAsync(uri, Wsman.ActionPull, null, pull, cancellationToken, EventPullWait)
                        .ConfigureAwait(false)).Element(Wsman.Enumeration + "PullResponse");
                }
                catch (WsmanTimedOutException)
                {
                    // Nothing happened during the wait: pull again.
                }

                if (pulled is null)
                    continue;

                context = pulled.Element(Wsman.Enumeration + "EnumerationContext")?.Value.Trim() ?? context;
                foreach (var item in Child(pulled, "Items")?.Elements() ?? [])
                {
                    var (instance, epr) = SplitItem(item);
                    var eventClass = WsmanObjectReader.ClassOfQuery(eventQuery);
                    var schemas = await SchemasForAsync(instance, eventClass).ConfigureAwait(false);
                    yield return WsmanObjectReader.Read(instance, epr, null, _host, _namespacePath, eventClass,
                        schemas);
                }
            }
        }
        finally
        {
            try
            {
                await _client.CallAsync(uri, Wsman.ActionUnsubscribe, null,
                        new XElement(Wsman.Eventing + "Unsubscribe"), CancellationToken.None,
                        extraHeaders: subscription)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsBestEffortFailure(ex))
            {
                // Best effort, as for Release.
            }
        }
    }

    /// <summary>
    /// The headers that address the subscription in an Unsubscribe: the reference properties and parameters of the reply's
    /// SubscriptionManager, which WS-Addressing (2004/08) sends back as headers, chiefly its <c>e:Identifier</c>. Windows
    /// puts them in <c>ReferenceProperties</c>. The resource URI among them is already in every request's header.
    /// </summary>
    internal static List<XElement> SubscriptionHeaders(XElement subscribeResponse)
    {
        var manager = subscribeResponse.Element(Wsman.Eventing + "SubscriptionManager");
        if (manager is null)
            return [];

        return
        [
            .. manager.Elements(Wsman.Addressing + "ReferenceProperties")
                .Concat(manager.Elements(Wsman.Addressing + "ReferenceParameters"))
                .SelectMany(e => e.Elements())
                .Where(e => e.Name != Wsman.Man + "ResourceURI"),
        ];
    }

    /// <summary>A cleanup request that failed in a way not worth reporting; an HTTP timeout surfaces as a cancellation.</summary>
    private static bool IsBestEffortFailure(Exception ex) =>
        ex is WsmanException or WmiException or HttpRequestException or OperationCanceledException;

    public async Task PutInstanceAsync(WmiObject instance, WbemChangeFlags flags, CancellationToken cancellationToken)
    {
        var uri = _client.ResourceUri(instance.ClassName);
        var body = BuildInstanceElement(instance, uri, await SchemaAsync(instance.ClassName).ConfigureAwait(false),
            await EmbeddedSchemasAsync(instance.Properties.Values).ConfigureAwait(false));
        var selectors = instance.KeyPropertyNames
            .Where(k => instance.Properties.GetValueOrDefault(k) is not null)
            .Select(k => new KeyValuePair<string, string>(k, WsmanClient.Format(instance.Properties[k]!)))
            .ToList();

        if (flags == WbemChangeFlags.CreateOnly)
        {
            await _client.CallAsync(uri, Wsman.ActionCreate, null, body, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _client.CallAsync(uri, Wsman.ActionPut, selectors, body, cancellationToken).ConfigureAwait(false);
        }
        catch (WmiException ex) when (flags == WbemChangeFlags.CreateOrUpdate &&
                                      ex.HResult == unchecked((int)WbemStatus.NotFound))
        {
            await _client.CallAsync(uri, Wsman.ActionCreate, null, body, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DeleteInstanceAsync(string objectPath, CancellationToken cancellationToken)
    {
        var path = Parse(objectPath);
        return _client.CallAsync(_client.ResourceUri(path.ClassName), Wsman.ActionDelete, Selectors(path), null,
            cancellationToken);
    }

    public async Task<WmiObject?> InvokeMethodAsync(
        string objectPath, string methodName, IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        var path = Parse(objectPath);
        var uri = $"{Wsman.DmtfSchema}{path.ClassName}";
        XNamespace p = uri;
        var input = new XElement(p + $"{methodName}_INPUT",
            new XAttribute(XNamespace.Xmlns + "xsi", Wsman.Xsi),
            new XAttribute(Wsman.Xsi + "type", $"{methodName}_INPUT_Type"));
        // The class definition says which parameters are datetimes, and what the method returns.
        var methodSchema = (await SchemaAsync(path.ClassName).ConfigureAwait(false))?.Method(methodName);
        var embeddedSchemas = await EmbeddedSchemasAsync(parameters?.Values ?? []).ConfigureAwait(false);
        foreach (var (name, value) in parameters ?? new Dictionary<string, object?>())
            AppendValue(input, p + name, ForWire(value, name, methodSchema), embeddedSchemas);

        // Windows' own client names the parameters it sets; the provider uses the list to tell set from unset.
        List<WsmanOption> options =
        [
            .. (parameters ?? new Dictionary<string, object?>()).Keys
            .Select(k => new WsmanOption("wmiarray:__MIInstanceModifiedPropertiesList", "xs:string", k)),

            new("wmi:__MI_OPERATIONOPTIONS_WRITEERRORMODE", "xs:unsignedInt", "1"),
            new("msftwinrm:UsePreciseArrays", "xs:boolean", "true")
        ];
        var body = await _client.CallAsync(uri, $"{uri}/{methodName}",
            [new KeyValuePair<string, string>("__cimnamespace", _client.Namespace), .. Selectors(path)],
            input, cancellationToken, options: options).ConfigureAwait(false);
        var output = body.Elements().FirstOrDefault();
        if (output is null)
            return null;

        var inner = await SchemasForAsync(output, null).ConfigureAwait(false);
        var outputName = output.Name.LocalName;

        var read = WsmanObjectReader.Read(output, null, [], _host, _namespacePath, null, Lookup);
        return new WmiObject("__PARAMETERS", read.Properties, [], _host, _namespacePath);

        WsmanClassSchema? Lookup(string name) =>
            name == outputName && methodSchema is not null ? methodSchema : inner?.Invoke(name);
    }

    private const string CimXmlResource = "http://schemas.dmtf.org/wbem/cim-xml/2/cim-schema/2/*";

    /// <summary>The class's property and parameter types, fetched once; null when the listener does not give the class.</summary>
    private async Task<WsmanClassSchema?> SchemaAsync(string className)
    {
        var fetch = _schemas.GetOrAdd(className, FetchSchemaAsync);
        try
        {
            return await fetch.ConfigureAwait(false);
        }
        catch
        {
            // A transport failure (a dropped connection, a timeout) says nothing about the class: try again next time.
            _schemas.TryRemove(new KeyValuePair<string, Task<WsmanClassSchema?>>(className, fetch));
            throw;
        }
    }

    private async Task<WsmanClassSchema?> FetchSchemaAsync(string className)
    {
        try
        {
            var body = await _client.CallAsync(CimXmlResource, Wsman.ActionGet,
                [new KeyValuePair<string, string>("__cimnamespace", _client.Namespace), new("ClassName", className)], null, CancellationToken.None,
                null,
                [
                    new WsmanOption("IncludeInheritanceHierarchy", "xs:boolean", "true"),
                    new WsmanOption("IncludeInheritedElements", "xs:boolean", "true"),
                    new WsmanOption("IncludeQualifiers", "xs:boolean", "false"),
                ]).ConfigureAwait(false);
            var cls = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "CLASS");
            return cls is null ? null : WsmanClassSchema.Parse(cls);
        }
        catch (Exception ex) when (ex is WsmanException or WmiException)
        {
            // Untyped is better than failing the read (system and event classes, say, may not answer).
            return null;
        }
    }

    /// <summary>
    /// The schemas an instance needs: its class and the classes of the instances nested in it (their <c>xsi:type</c>),
    /// fetched ahead so that reading it is synchronous.
    /// </summary>
    private async Task<Func<string, WsmanClassSchema?>?> SchemasForAsync(XElement instance, string? fallbackClass)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var top = instance.Name.LocalName == "XmlFragment" ? fallbackClass : instance.Name.LocalName;
        if (!string.IsNullOrEmpty(top) && !top.EndsWith("_OUTPUT", StringComparison.Ordinal))
            names.Add(top);

        foreach (var nested in instance.Descendants())
            if (WsmanObjectReader.TypeClassOf(nested) is { } nestedClass)
                names.Add(nestedClass);

        var resolved = new Dictionary<string, WsmanClassSchema?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            resolved[name] = await SchemaAsync(name).ConfigureAwait(false);

        return resolved.GetValueOrDefault;
    }

    private static XElement BuildInstanceElement(
        WmiObject instance, string uri, WsmanClassSchema? schema, Func<string, WsmanClassSchema?> embeddedSchemas)
    {
        XNamespace p = uri;
        var element = new XElement(p + instance.ClassName);
        foreach (var (name, value) in instance.Properties)
            AppendValue(element, p + name, ForWire(value, name, schema), embeddedSchemas);

        return element;
    }

    /// <summary>The schemas of the embedded objects among <paramref name="values"/>, at any depth, fetched ahead so that encoding them is synchronous.</summary>
    private async Task<Func<string, WsmanClassSchema?>> EmbeddedSchemasAsync(IEnumerable<object?> values)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectEmbeddedClasses(values, names);

        var resolved = new Dictionary<string, WsmanClassSchema?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            resolved[name] = await SchemaAsync(name).ConfigureAwait(false);

        return resolved.GetValueOrDefault;
    }

    private static void CollectEmbeddedClasses(IEnumerable<object?> values, HashSet<string> names)
    {
        foreach (var value in values)
            switch (value)
            {
                case WmiObject embedded:
                    names.Add(embedded.ClassName);
                    CollectEmbeddedClasses(embedded.Properties.Values, names);
                    break;
                case string or byte[]:
                    break;
                case IEnumerable sequence:
                    CollectEmbeddedClasses(sequence.Cast<object?>(), names);
                    break;
            }
    }

    /// <summary>
    /// A datetime is read as a DMTF string, as over DCOM, but the listener only takes it as <c>cim:Datetime</c> or <c>cim:Interval</c>: sent as
    /// a string it fails schema validation. Only the class definition tells such a string from any other.
    /// </summary>
    internal static object? ForWire(object? value, string name, WsmanClassSchema? schema)
    {
        if (schema is null || !schema.TryGetProperty(name, out var type) || type.CimType != "datetime")
            return value;

        return value switch
        {
            string text => DmtfDateTime.Parse(text),
            IEnumerable sequence => sequence.Cast<object?>()
                .Select(e => e is string text ? DmtfDateTime.Parse(text) : e).ToArray(),
            _ => value,
        };
    }

    /// <param name="parent">The element to add to.</param>
    /// <param name="name">The name of the value's element.</param>
    /// <param name="value">The value, already through <see cref="ForWire"/> if it is a top-level one.</param>
    /// <param name="embeddedSchemas">The schema of an embedded object's class, or null when it is not known.</param>
    internal static void AppendValue(XElement parent, XName name, object? value,
        Func<string, WsmanClassSchema?>? embeddedSchemas = null)
    {
        switch (value)
        {
            case null:
                parent.Add(new XElement(name, new XAttribute(Wsman.Xsi + "nil", "true")));
                break;
            case WmiObject embedded:
                var nested = new XElement(name);
                XNamespace ns = $"{Wsman.DmtfSchema}{embedded.ClassName}";
                nested.Add(new XAttribute(XNamespace.Xmlns + "i", ns),
                    new XAttribute(Wsman.Xsi + "type", $"i:{embedded.ClassName}_Type"));
                // The provider rejects an embedded object that spells out its unset properties as nil.
                var schema = embeddedSchemas?.Invoke(embedded.ClassName);
                foreach (var (childName, childValue) in embedded.Properties.Where(p => p.Value is not null))
                    AppendValue(nested, ns + childName, ForWire(childValue, childName, schema), embeddedSchemas);

                parent.Add(nested);
                break;
            case string text:
                parent.Add(TypedElement(name, text, "cimString"));
                break;
            case DateTimeOffset moment:
                parent.Add(new XElement(name, new XElement(Wsman.Cim + "Datetime",
                    moment.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture))));
                break;
            case TimeSpan interval:
                ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
                parent.Add(new XElement(name, new XElement(Wsman.Cim + "Interval", XmlConvert.ToString(interval))));
                break;
            case IEnumerable sequence:
                foreach (var item in sequence)
                    AppendValue(parent, name, item, embeddedSchemas);

                break;
            default:
                parent.Add(TypedElement(name, WsmanClient.Format(value), CimTypeName(value)));
                break;
        }
    }

    /// <summary>
    /// A value with its CIM type, as Windows' own client sends it (<c>xsi:type="cim:cimUnsignedShort"</c>). Without the
    /// type the provider has to guess and an embedded object's numeric properties are sometimes rejected.
    /// </summary>
    private static XElement TypedElement(XName name, string text, string? cimType)
    {
        var element = new XElement(name, text);
        if (cimType is not null)
            element.Add(new XAttribute(Wsman.Xsi + "type", "cim:" + cimType));

        return element;
    }

    private static string? CimTypeName(object value) => value switch
    {
        byte => "cimUnsignedByte",
        sbyte => "cimByte",
        ushort => "cimUnsignedShort",
        short => "cimShort",
        uint => "cimUnsignedInt",
        int => "cimInt",
        ulong => "cimUnsignedLong",
        long => "cimLong",
        bool => "cimBoolean",
        float => "cimFloat",
        double => "cimDouble",
        char => "cimChar16",
        _ => null,
    };

    private static CimObjectPath Parse(string objectPath)
    {
        if (CimObjectPath.TryParse(objectPath, out var parsed))
            return parsed;

        throw new ArgumentException($"'{objectPath}' is not a valid CIM object path.", nameof(objectPath));
    }

    private static List<KeyValuePair<string, string>> Selectors(CimObjectPath path) =>
        [.. path.Keys.Select(k => new KeyValuePair<string, string>(k.Key, WsmanClient.Format(k.Value)))];

    public async ValueTask DisposeAsync()
    {
        // The lease is shared with the namespaces opened from this one, so it must be released exactly once.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await _lease.ReleaseAsync().ConfigureAwait(false);
    }

    /// <summary>The namespaces opened from one connection share its HTTP session, which closes with the last of them.</summary>
    private sealed class Lease(WsmanTransport transport)
    {
        private int _holders = 1;

        public WsmanTransport Transport { get; } = transport;

        public void Acquire() => Interlocked.Increment(ref _holders);

        public ValueTask ReleaseAsync()
        {
            if (Interlocked.Decrement(ref _holders) == 0)
                Transport.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
