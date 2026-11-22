using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NWmi.Wmi;

namespace NWmi.WinRm;

/// <summary>
/// What <see cref="WmiConnection"/> runs on when it is connected over WinRM instead of DCOM: the same operations
/// as WS-Management requests against the Windows WMI provider (Get/Put/Create/Delete for instances, Enumerate and
/// Pull for queries, Invoke for methods, a pull subscription for events).
/// </summary>
internal sealed partial class WinRmBackend : IAsyncDisposable
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

    public async Task<WmiInstance> GetInstanceAsync(string objectPath, CancellationToken cancellationToken)
    {
        var path = Parse(objectPath);
        var body = await _client
            .CallAsync(_client.ResourceUri(path.ClassName), Wsman.ActionGet, Selectors(path), null, cancellationToken)
            .ConfigureAwait(false);
        var instance = body.Elements().FirstOrDefault() ?? throw new WsmanException("The Get reply holds no instance.");
        var schemas = await SchemasForAsync(instance, path.ClassName).ConfigureAwait(false);
        return WsmanObjectReader.Read(instance, null, [.. path.Keys.Select(k => k.Key)], _host, _namespacePath,
            path.ClassName, schemas);
    }

    /// <summary>A class's definition from the CIM-XML resource; the listener's "the class does not exist" is not-found, as over DCOM.</summary>
    public async Task<WmiClass> GetClassAsync(string className, CancellationToken cancellationToken)
    {
        WsmanClassSchema schema;
        try
        {
            schema = await FetchClassAsync(className, cancellationToken).ConfigureAwait(false);
        }
        catch (WmiException ex) when (ex.HResult == unchecked((int)WbemStatus.InvalidClass))
        {
            throw new WmiException(unchecked((int)WbemStatus.NotFound),
                $"The class {className} does not exist in the {_client.Namespace} namespace");
        }

        _schemas[className] = Task.FromResult<WsmanClassSchema?>(schema);
        return schema.Class;
    }

    public IAsyncEnumerable<WmiInstance> QueryAsync(string query, CancellationToken cancellationToken) =>
        EnumerateAsync(query, cancellationToken);

    /// <summary>WS-Management has no class-enumeration call that takes a depth, so this is the equivalent WQL; <paramref name="className"/> is already checked to be a plain identifier.</summary>
    public IAsyncEnumerable<WmiInstance> EnumerateInstancesAsync(
        string className, bool shallow, CancellationToken cancellationToken) =>
        EnumerateAsync(
            shallow ? $"SELECT * FROM {className} WHERE __CLASS = '{className}'" : $"SELECT * FROM {className}",
            cancellationToken);

    /// <summary>
    /// The subclasses of <paramref name="superClass"/> (every class when null), from the CIM-XML resource, which gives each
    /// class's full definition. The listener ignores <c>DeepInheritance</c> and always lists every descendant, so shallow keeps
    /// the direct subclasses here.
    /// </summary>
    public async IAsyncEnumerable<WmiClass> EnumerateClassesAsync(
        string? superClass, bool shallow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> selectors = [new("__cimnamespace", _client.Namespace)];
        if (!string.IsNullOrEmpty(superClass))
            selectors.Add(new KeyValuePair<string, string>("ClassName", superClass));

        var enumerate = new XElement(Wsman.Enumeration + "Enumerate",
            new XElement(Wsman.Man + "OptimizeEnumeration"),
            new XElement(Wsman.Man + "MaxElements", _batchSize));
        await foreach (var (item, _) in EnumerateItemsAsync(CimXmlResource, selectors, enumerate, ClassOptions,
                           cancellationToken).ConfigureAwait(false))
        {
            if (item.Name.LocalName != "CLASS")
                continue;

            var @class = WsmanClassSchema.Parse(item, _host, _namespacePath).Class;
            if (!shallow || string.Equals(@class.SuperClassName ?? string.Empty, superClass ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                yield return @class;
        }
    }

    /// <summary>A query whose results are classes, not instances (<c>meta_class</c>, <c>SchemaOnly</c>, <c>ClassDefsOnly</c>).</summary>
    internal static bool IsSchemaQuery(string query) => SchemaQuery().IsMatch(query);

    [GeneratedRegex(@"\b(meta_class|SchemaOnly|ClassDefsOnly)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SchemaQuery();

    private async IAsyncEnumerable<WmiInstance> EnumerateAsync(
        string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerate = new XElement(Wsman.Enumeration + "Enumerate",
            new XElement(Wsman.Man + "OptimizeEnumeration"),
            new XElement(Wsman.Man + "MaxElements", _batchSize),
            new XElement(Wsman.Man + "Filter", new XAttribute("Dialect", Wsman.WqlDialect), query),
            new XElement(Wsman.Man + "EnumerationMode", "EnumerateObjectAndEPR"));
        var queryClass = WsmanObjectReader.ClassOfQuery(query);
        var isSchema = IsSchemaQuery(query);
        await foreach (var (instance, epr) in EnumerateItemsAsync(_client.ResourceUri("*"), null, enumerate, null,
                           cancellationToken).ConfigureAwait(false))
        {
            // Classes hold no values worth typing, and there can be thousands of them.
            var schemas = isSchema ? null : await SchemasForAsync(instance, queryClass).ConfigureAwait(false);
            yield return WsmanObjectReader.Read(instance, epr, null, _host, _namespacePath, queryClass, schemas,
                isSchema);
        }
    }

    /// <summary>Enumerate, then Pull until the end of the sequence; an enumeration left early is released.</summary>
    private async IAsyncEnumerable<(XElement Item, XElement? Epr)> EnumerateItemsAsync(
        string uri, List<KeyValuePair<string, string>>? selectors, XElement enumerate, IEnumerable<WsmanOption>? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = await _client
            .CallAsync(uri, Wsman.ActionEnumerate, selectors, enumerate, cancellationToken, options: options)
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
                    yield return SplitItem(item);

                if (finished)
                    yield break;

                cancellationToken.ThrowIfCancellationRequested();
                var pull = new XElement(Wsman.Enumeration + "Pull",
                    new XElement(Wsman.Enumeration + "EnumerationContext", context),
                    new XElement(Wsman.Enumeration + "MaxElements", _batchSize));
                var next = await _client.CallAsync(uri, Wsman.ActionPull, selectors, pull, cancellationToken)
                    .ConfigureAwait(false);
                response = next.Element(Wsman.Enumeration + "PullResponse")
                           ?? throw new WsmanException("The Pull reply has no PullResponse.");
            }
        }
        finally
        {
            if (!finished && !string.IsNullOrEmpty(context))
                await ReleaseAsync(uri, selectors, context).ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync(string uri, List<KeyValuePair<string, string>>? selectors, string context)
    {
        try
        {
            await _client.CallAsync(uri, Wsman.ActionRelease, selectors,
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

    public async IAsyncEnumerable<WmiInstance> WatchAsync(
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

    /// <summary>The selectors that name <paramref name="instance"/>: its keys as a path holds them (a datetime as DMTF, a reference as its path).</summary>
    internal static List<KeyValuePair<string, string>> KeySelectors(WmiInstance instance) =>
    [
        .. instance.KeyNames
            .Select(k => instance.Properties[k]!)
            .Where(p => p.Value is not null)
            .Select(p => new KeyValuePair<string, string>(p.Name, WsmanClient.Format(WmiValues.ToKeyValue(p.Value!)))),
    ];

    public async Task PutInstanceAsync(WmiInstance instance, WbemChangeFlags flags, CancellationToken cancellationToken)
    {
        var className = instance.SystemProperties.ClassName;
        var uri = _client.ResourceUri(className);
        var body = BuildInstanceElement(instance, uri, await SchemaAsync(className).ConfigureAwait(false),
            await EmbeddedSchemasAsync(instance.Properties.Select(p => p.Value)).ConfigureAwait(false));
        var selectors = KeySelectors(instance);

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

    public async Task<WmiInstance?> InvokeMethodAsync(
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
        return WmiInstance.FromWinRm("__PARAMETERS",
            read.Properties.Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)), methodSchema?.Class, [],
            _host, _namespacePath);

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
            return await FetchClassAsync(className, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WsmanException or WmiException)
        {
            // Untyped is better than failing the read (system and event classes, say, may not answer).
            return null;
        }
    }

    /// <summary>The options of a class Get or enumeration: every property, inherited ones too, with its qualifiers.</summary>
    private static readonly WsmanOption[] ClassOptions =
    [
        new("IncludeInheritanceHierarchy", "xs:boolean", "true"),
        new("IncludeInheritedElements", "xs:boolean", "true"),
        new("IncludeQualifiers", "xs:boolean", "true"),
    ];

    private async Task<WsmanClassSchema> FetchClassAsync(string className, CancellationToken cancellationToken)
    {
        var body = await _client.CallAsync(CimXmlResource, Wsman.ActionGet,
                [new KeyValuePair<string, string>("__cimnamespace", _client.Namespace), new("ClassName", className)],
                null, cancellationToken, null, ClassOptions)
            .ConfigureAwait(false);
        var cls = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "CLASS")
                  ?? throw new WsmanException($"The reply to the Get of class {className} holds no class.");
        return WsmanClassSchema.Parse(cls, _host, _namespacePath);
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
        WmiInstance instance, string uri, WsmanClassSchema? schema, Func<string, WsmanClassSchema?> embeddedSchemas)
    {
        XNamespace p = uri;
        var element = new XElement(p + instance.SystemProperties.ClassName);
        foreach (var property in instance.Properties)
            AppendValue(element, p + property.Name, ForWire(property.Value, property.Name, schema), embeddedSchemas);

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
                case WmiInstance { IsReference: true }:
                    break;
                case WmiInstance embedded:
                    names.Add(embedded.SystemProperties.ClassName);
                    CollectEmbeddedClasses(embedded.Properties.Select(p => p.Value), names);
                    break;
                case string or byte[]:
                    break;
                case IEnumerable sequence:
                    CollectEmbeddedClasses(sequence.Cast<object?>(), names);
                    break;
            }
    }

    /// <summary>
    /// A value in the form the listener takes for its declared type. A datetime goes as <c>cim:Datetime</c> or
    /// <c>cim:Interval</c>, so one given as a DMTF string is parsed: sent as a string it fails schema validation, and only
    /// the class definition tells such a string from any other. A reference goes as its path, including one given as an
    /// instance that was not read from a reference.
    /// </summary>
    internal static object? ForWire(object? value, string name, WsmanClassSchema? schema)
    {
        if (schema is null || !schema.TryGetProperty(name, out var type))
            return value;

        return type.CimType switch
        {
            "datetime" => Each(value, e => e is string text ? DmtfDateTime.Parse(text) : e),
            "reference" => Each(value, e => e is WmiInstance reference ? WmiValues.ReferencePath(reference) : e),
            _ => value,
        };

        static object? Each(object? value, Func<object?, object?> convert) =>
            value is string || value is not IEnumerable sequence
                ? convert(value)
                : sequence.Cast<object?>().Select(convert).ToArray();
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
            case WmiInstance { IsReference: true } reference:
                parent.Add(TypedElement(name, WmiValues.ReferencePath(reference), "cimString"));
                break;
            case WmiInstance embedded:
                var nested = new XElement(name);
                var embeddedClass = embedded.SystemProperties.ClassName;
                XNamespace ns = $"{Wsman.DmtfSchema}{embeddedClass}";
                nested.Add(new XAttribute(XNamespace.Xmlns + "i", ns),
                    new XAttribute(Wsman.Xsi + "type", $"i:{embeddedClass}_Type"));
                // The provider rejects an embedded object that spells out its unset properties as nil.
                var schema = embeddedSchemas?.Invoke(embeddedClass);
                foreach (var child in embedded.Properties.Where(p => p.Value is not null))
                    AppendValue(nested, ns + child.Name, ForWire(child.Value, child.Name, schema), embeddedSchemas);

                parent.Add(nested);
                break;
            case string text:
                parent.Add(TypedElement(name, text, "cimString"));
                break;
            case DateTimeOffset moment:
                parent.Add(new XElement(name, new XElement(Wsman.Cim + "Datetime",
                    moment.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture))));
                break;
            case DateTime moment:
                AppendValue(parent, name, new DateTimeOffset(moment.ToUniversalTime(), TimeSpan.Zero), embeddedSchemas);
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

    private static WmiObjectPath Parse(string objectPath)
    {
        if (WmiObjectPath.TryParse(objectPath, out var parsed))
            return parsed;

        throw new ArgumentException($"'{objectPath}' is not a valid CIM object path.", nameof(objectPath));
    }

    private static List<KeyValuePair<string, string>> Selectors(WmiObjectPath path) =>
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
