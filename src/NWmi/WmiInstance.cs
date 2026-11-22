using NWmi.Generic;
using NWmi.Wmio;

namespace NWmi;

/// <summary>
/// A CIM instance: its class, its properties with their current values, and where it lives. A query, enumeration, Get or
/// event returns these; a class that a query returns in place of an instance (<c>SELECT * FROM meta_class</c>) is one too,
/// with no path and every value null, and its definition in <see cref="Class"/>.
/// </summary>
/// <remarks>
/// To create an instance on the server, make one from its class (<see cref="WmiInstance(WmiClass)"/> with a class from
/// <see cref="WmiConnection.GetClassAsync"/>), set its properties, and pass it to <see cref="WmiConnection.PutInstanceAsync"/>.
/// </remarks>
public sealed class WmiInstance
{
    private readonly WmiKeyedCollection<WmiProperty> _properties;
    private readonly WmioClass? _wire;
    private WmiClass? _class;
    private readonly bool _clientOnly;
    private readonly bool _isClassRow;
    private readonly bool _isSingleton;
    private readonly string? _namespace;
    private readonly string? _server;
    private readonly List<WmioQualifier> _qualifiers;
    private readonly Dictionary<string, List<WmioQualifier>> _propertyQualifiers;

    /// <summary>
    /// A new instance of <paramref name="class"/>, its values the class's defaults, none of them marked modified. It is in the
    /// class's namespace.
    /// </summary>
    /// <param name="class">The class, from <see cref="WmiConnection.GetClassAsync"/> or another instance's <see cref="Class"/>.</param>
    public WmiInstance(WmiClass @class)
    {
        ArgumentNullException.ThrowIfNull(@class);
        _class = @class;
        _wire = @class.Wire;
        var system = @class.SystemProperties;
        _namespace = system.Namespace;
        _server = system.Server;
        _qualifiers = [];
        _propertyQualifiers = new Dictionary<string, List<WmioQualifier>>(StringComparer.OrdinalIgnoreCase);
        _properties = Collection(@class.Properties.Select(p =>
            new WmiProperty(p.Name, CopyValue(p.Value), p.Type, p.Flags, isValueModified: false)));
    }

    /// <summary>
    /// An instance built on the client, with no class from the server: add its properties with
    /// <see cref="WmiProperty.Create(string, object?, WmiType, WmiFlags)"/>, marking its keys with <see cref="WmiFlags.Key"/>.
    /// Its <see cref="Class"/> is made from the properties added.
    /// </summary>
    /// <param name="className">The class name.</param>
    /// <param name="namespace">The namespace (<c>root\cimv2</c>), if known.</param>
    /// <exception cref="ArgumentException"><paramref name="className"/> is empty.</exception>
    public WmiInstance(string className, string? @namespace = null)
        : this(className, @namespace, server: null, isSingleton: false, isReference: false)
    {
    }

    private WmiInstance(string className, string? @namespace, string? server, bool isSingleton, bool isReference)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);
        _clientOnly = true;
        ClientOnlyClassName = className;
        _namespace = @namespace;
        _server = server;
        _isSingleton = isSingleton;
        IsReference = isReference;
        _qualifiers = [];
        _propertyQualifiers = new Dictionary<string, List<WmioQualifier>>(StringComparer.OrdinalIgnoreCase);
        _properties = Collection([]);
    }

    /// <summary>
    /// The value of a reference: an instance built on the client from the path it holds, with the path's class, namespace
    /// and server, and a key property for each of the path's keys.
    /// </summary>
    internal static WmiInstance FromReference(WmiObjectPath path)
    {
        var instance = new WmiInstance(path.ClassName, path.Namespace, path.Server, path.IsSingleton,
            isReference: true);
        foreach (var (name, value) in path.Keys)
        {
            var type = WmiTypeMapping.ToWmiType(WmiConnection.InferCimType(value));
            instance._properties.Add(new WmiProperty(name, WmiValues.ToPublic(value, type), type,
                WmiFlags.Property | WmiFlags.Key, isValueModified: false));
        }

        return instance;
    }

    /// <summary>Whether this instance is the value of a reference, made from the path it held (<see cref="FromReference"/>).</summary>
    internal bool IsReference { get; }

    /// <summary>A copy of <paramref name="instance"/>: the same class, copies of its values (embedded instances too), the same modified marks.</summary>
    public WmiInstance(WmiInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _wire = instance._wire;
        _class = instance._clientOnly ? null : instance._class;
        _clientOnly = instance._clientOnly;
        ClientOnlyClassName = instance.ClientOnlyClassName;
        _isClassRow = instance._isClassRow;
        _isSingleton = instance._isSingleton;
        IsReference = instance.IsReference;
        _namespace = instance._namespace;
        _server = instance._server;
        _qualifiers = [.. instance._qualifiers];
        _propertyQualifiers = instance._propertyQualifiers.ToDictionary(p => p.Key, p => p.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
        _properties = Collection(instance._properties.Select(p => p.Copy()));
    }

    private WmiInstance(WmiClass? @class, WmioClass? wire, IEnumerable<WmiProperty> properties, bool isClassRow,
        string? server, string? @namespace, IEnumerable<WmioQualifier>? qualifiers = null,
        IReadOnlyDictionary<string, IReadOnlyList<WmioQualifier>>? propertyQualifiers = null)
    {
        _class = @class;
        _wire = wire;
        _isClassRow = isClassRow;
        _server = server;
        _namespace = @namespace;
        _qualifiers = [.. qualifiers ?? []];
        _propertyQualifiers = new Dictionary<string, List<WmioQualifier>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (property, set) in propertyQualifiers ?? new Dictionary<string, IReadOnlyList<WmioQualifier>>())
            if (set.Count > 0)
                _propertyQualifiers[property] = [.. set];

        _properties = Collection(properties);
    }

    private WmiKeyedCollection<WmiProperty> Collection(IEnumerable<WmiProperty> properties) =>
        new(properties, p => p.Name, _ =>
        {
            if (!_clientOnly)
                throw new InvalidOperationException(
                    $"'{ClassName}' has a class from the server; only an instance built on the client takes new properties.");

            _class = null;
        });

    /// <summary>
    /// An object read in the binary object format: an instance, or a class that a query returned in place of one. The server
    /// and namespace come from the encoding when it carries them, otherwise from the connection.
    /// </summary>
    internal static WmiInstance FromWire(WmioObjectBlock block, string? serverHint, string? namespaceHint)
    {
        var server = block.Decoration?.Server ?? serverHint;
        var ns = block.Decoration?.Namespace ?? namespaceHint;
        if (block.IsClass)
        {
            var @class = block.Class!;
            var isParameters = IsParameters(@class.Name);
            return new WmiInstance(WmiClass.FromWire(@class, server, ns), @class,
                @class.Properties.Select(p => new WmiProperty(p.Name, null, WmiTypeMapping.ToWmiType(p.Type),
                    WmiTypeMapping.DeclarationFlags(p.Qualifiers, isParameters), isValueModified: false)),
                isClassRow: true, server, ns);
        }

        var instance = block.Instance!;
        var isParameterSet = IsParameters(instance.Class.Name);
        return new WmiInstance(null, instance.Class,
            instance.Class.Properties.Select(p =>
            {
                var type = WmiTypeMapping.ToWmiType(p.Type);
                return new WmiProperty(p.Name,
                    WmiValues.ToPublic(FromWireValue(instance.Values.GetValueOrDefault(p.Name), server, ns), type),
                    type, WmiTypeMapping.DeclarationFlags(p.Qualifiers, isParameterSet), isValueModified: false);
            }),
            isClassRow: false, server, ns, instance.Qualifiers, instance.PropertyQualifiers);
    }

    /// <summary>
    /// An instance read over WinRM, which carries values but no class: <paramref name="class"/> is the class definition the
    /// server gave, or null to make one from the values, with the keys named by <paramref name="keyNames"/> (the reply's
    /// selectors).
    /// </summary>
    internal static WmiInstance FromWinRm(string className, IEnumerable<KeyValuePair<string, object?>> values,
        WmiClass? @class, IReadOnlyList<string> keyNames, string? server, string? @namespace, bool isClassRow = false)
    {
        var isParameters = IsParameters(className);
        var properties = new List<WmiProperty>();
        foreach (var (name, value) in values)
        {
            var declared = @class?.Properties[name];
            WmiType type;
            WmiFlags flags;
            if (declared is not null)
            {
                type = declared.Type;
                flags = declared.Flags;
            }
            else
            {
                type = WmiType.Unknown;
                flags = isParameters ? WmiFlags.Parameter : WmiFlags.Property;
                if (keyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    flags |= WmiFlags.Key;
            }

            // With no class definition, or one that types an embedded object as a plain string (no EmbeddedObject or
            // EmbeddedInstance qualifier), the value tells.
            if (type is WmiType.Unknown or WmiType.String or WmiType.StringArray)
                type = value switch
                {
                    WmiInstance { IsReference: true } when type == WmiType.Unknown => WmiType.Reference,
                    WmiInstance { IsReference: false } => WmiType.Instance,
                    DateTime or TimeSpan when type == WmiType.Unknown => WmiType.DateTime,
                    object?[] { Length: > 0 } array when type == WmiType.Unknown &&
                                                         array.All(e => e is WmiInstance { IsReference: true }) =>
                        WmiType.ReferenceArray,
                    object?[] { Length: > 0 } array when type == WmiType.Unknown &&
                                                         array.All(e => e is DateTime or TimeSpan) =>
                        WmiType.DateTimeArray,
                    object?[] { Length: > 0 } array when array.All(e => e is WmiInstance { IsReference: false }) =>
                        WmiType.InstanceArray,
                    _ => type,
                };

            properties.Add(new WmiProperty(declared?.Name ?? name, isClassRow ? null : WmiValues.ToPublic(value, type),
                type, flags, isValueModified: false));
        }

        @class ??= WmiClass.Synthesized(className,
            properties.Select(p => new WmiPropertyDeclaration(p.Name, p.Type, p.DeclaredFlags | WmiFlags.NullValue,
                new WmiReadOnlyKeyedCollection<WmiQualifier>([], q => q.Name), null, null)),
            server, @namespace);
        return new WmiInstance(@class, null, properties, isClassRow, server, @namespace);
    }

    private static bool IsParameters(string? className) =>
        string.Equals(className, "__PARAMETERS", StringComparison.OrdinalIgnoreCase);

    /// <summary>A value as read from the binary format, with each embedded object (at any depth in an array) as a <see cref="WmiInstance"/>.</summary>
    internal static object? FromWireValue(object? value, string? server, string? @namespace) => value switch
    {
        WmioObjectBlock embedded => FromWire(embedded, server, @namespace),
        object?[] array => array.Select(e => FromWireValue(e, server, @namespace)).ToArray(),
        _ => value,
    };

    internal static object? CopyValue(object? value) => value switch
    {
        WmiInstance embedded => new WmiInstance(embedded),
        WmiInstance[] instances => instances.Select(e => new WmiInstance(e)).ToArray(),
        // Only a true object?[] can hold embedded objects to copy; a string[] (an object?[] by covariance) stays a string[].
        object?[] array when array.GetType() == typeof(object[]) => array.Select(CopyValue).ToArray(),
        Array array => array.Clone(),
        _ => value,
    };

    private string? ClientOnlyClassName { get; }

    private string ClassName => ClientOnlyClassName ?? _class?.Name ?? _wire?.Name ?? string.Empty;

    /// <summary>
    /// The instance's class. For an instance read over DCOM it is the class the instance carries, which has no methods (use
    /// <see cref="WmiConnection.GetClassAsync"/> for those); over WinRM it is the class definition the server gives, or, when
    /// it gives none, one made from the values; for an instance built on the client, one made from its properties.
    /// </summary>
    public WmiClass Class => _class ??= _wire is not null
        ? WmiClass.FromWire(_wire, _server, _namespace)
        : WmiClass.Synthesized(ClassName,
            _properties.Select(p => new WmiPropertyDeclaration(p.Name, p.Type, p.DeclaredFlags,
                new WmiReadOnlyKeyedCollection<WmiQualifier>([], q => q.Name), null, null)),
            _server, _namespace);

    /// <summary>The properties with their values, in the class's order. Only an instance built on the client takes new ones (<see cref="WmiKeyedCollection{T}.Add"/>).</summary>
    public WmiKeyedCollection<WmiProperty> Properties => _properties;

    /// <summary>The class name, namespace, server and path of this instance; the path follows the current key values.</summary>
    public WmiSystemProperties SystemProperties
    {
        get
        {
            if (_isClassRow)
                return new WmiSystemProperties(ClassName, _namespace, _server, null, null);

            var relative = BuildPath(null, null).RelativePath;
            var path = _namespace is null ? null : BuildPath(_namespace, _server).ToString();
            return new WmiSystemProperties(ClassName, _namespace, _server, path, relative);
        }
    }

    /// <summary>The key values go into the path only for an instance that holds a value for every key property.</summary>
    private WmiObjectPath BuildPath(string? @namespace, string? server)
    {
        var keys = KeyProperties();
        var hasKeys = keys.Count > 0 && keys.All(k => k.Value is not null);
        return new WmiObjectPath(ClassName,
            hasKeys ? keys.Select(k => new KeyValuePair<string, object>(k.Name, WmiValues.ToKeyValue(k.Value!))) : null,
            @namespace, server, isSingleton: _isSingleton && !hasKeys);
    }

    private List<WmiProperty> KeyProperties() => [.. _properties.Where(p => (p.DeclaredFlags & WmiFlags.Key) != 0)];

    /// <summary>The names of the key properties, in the class's order.</summary>
    internal IReadOnlyList<string> KeyNames => [.. KeyProperties().Select(p => p.Name)];

    /// <summary>Gets or sets a property's value, by a name matched case-insensitively; a shortcut for <see cref="Properties"/>.</summary>
    /// <exception cref="KeyNotFoundException">Getting a property the instance does not have.</exception>
    /// <exception cref="ArgumentException">Setting a property the instance does not have, or a system property (<c>__PATH</c>, ...; see <see cref="SystemProperties"/>).</exception>
    public object? this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            return (_properties[name] ??
                    throw new KeyNotFoundException($"'{ClassName}' has no property named '{name}'."))
                .Value;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.StartsWith("__", StringComparison.Ordinal))
                throw new ArgumentException($"'{name}' is a system property and cannot be set.", nameof(name));

            (_properties[name] ??
             throw new ArgumentException($"'{ClassName}' has no property named '{name}'.", nameof(name)))
                .Value = value;
        }
    }

    /// <summary>
    /// The qualifiers set on one property of this instance (not the ones its class declares, such as <c>key</c>); property and
    /// qualifier names match case-insensitively. Empty when there are none. An instance read from the server keeps the ones it came
    /// with, and <see cref="WmiConnection.PutInstanceAsync"/> sends them back.
    /// </summary>
    /// <exception cref="ArgumentException">The instance has no such property.</exception>
    /// <exception cref="InvalidOperationException">The instance has no binary class definition: it was read over WinRM, which carries no qualifiers, or built on the client.</exception>
    public IReadOnlyDictionary<string, object?> GetPropertyQualifiers(string propertyName)
    {
        var property = RequireProperty(propertyName);
        return _propertyQualifiers.TryGetValue(property.Name, out var set)
            ? set.ToDictionary(q => q.Name, q => q.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets a qualifier on one property of this instance, replacing one of the same name (names match case-insensitively), for
    /// <see cref="WmiConnection.PutInstanceAsync"/> to send. The CIM type comes from the value: string, bool, or a
    /// number (<c>sbyte</c>, <c>byte</c>, <c>short</c>, <c>ushort</c>, <c>int</c>, <c>uint</c>, <c>long</c>, <c>ulong</c>,
    /// <c>float</c>, <c>double</c>), or a non-empty array of one of those (<c>string[]</c>, <c>int[]</c>, ...).
    /// </summary>
    /// <remarks>
    /// The server decides what to keep: a provider may ignore qualifiers on instances, and one that is not defined to
    /// allow them on that property can reject the whole call.
    /// </remarks>
    /// <exception cref="ArgumentException">The instance has no such property, the name is empty, or the value is null, an empty array, or of another type (to remove a qualifier use <see cref="RemovePropertyQualifier"/>).</exception>
    /// <exception cref="InvalidOperationException">The instance has no binary class definition.</exception>
    public void SetPropertyQualifier(string propertyName, string qualifierName, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(qualifierName);
        var property = RequireProperty(propertyName);
        var (type, stored) = InferQualifierValue(value);
        if (!_propertyQualifiers.TryGetValue(property.Name, out var set))
            set = _propertyQualifiers[property.Name] = [];

        var existing = set.FindIndex(q => string.Equals(q.Name, qualifierName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            set[existing] = set[existing] with { Type = type, Value = stored };
        else
            set.Add(new WmioQualifier(qualifierName, QualifierFlavor.None, type, stored));
    }

    /// <summary>Removes a qualifier set on one property of this instance; returns whether there was one.</summary>
    /// <exception cref="ArgumentException">The instance has no such property.</exception>
    /// <exception cref="InvalidOperationException">The instance has no binary class definition.</exception>
    public bool RemovePropertyQualifier(string propertyName, string qualifierName)
    {
        ArgumentException.ThrowIfNullOrEmpty(qualifierName);
        var property = RequireProperty(propertyName);
        if (!_propertyQualifiers.TryGetValue(property.Name, out var set))
            return false;

        var removed = set.RemoveAll(q => string.Equals(q.Name, qualifierName, StringComparison.OrdinalIgnoreCase)) > 0;
        if (set.Count == 0)
            _propertyQualifiers.Remove(property.Name);

        return removed;
    }

    private WmioClass RequireWireClass() =>
        _wire ?? throw new InvalidOperationException(_clientOnly
            ? $"'{ClassName}' was built on the client and has no binary class definition; make it from a class from GetClassAsync on a DCOM connection."
            : $"'{ClassName}' has no binary class definition: it was read over WinRM, or made from a class read over WinRM.");

    private WmioProperty RequireProperty(string propertyName) =>
        RequireWireClass().FindProperty(propertyName)
        ?? throw new ArgumentException($"'{ClassName}' has no property named '{propertyName}'.", nameof(propertyName));

    private static (WbemType Type, object Value) InferQualifierValue(object? value)
    {
        if (value is null)
            throw new ArgumentException("A qualifier value cannot be null; remove the qualifier instead.",
                nameof(value));

        if (value is not string && value is System.Collections.IEnumerable sequence)
        {
            var items = sequence.Cast<object?>().ToArray();
            if (items.Length == 0)
                throw new ArgumentException("A qualifier array needs at least one element (its type comes from them).",
                    nameof(value));

            var (elementType, _) = InferQualifierValue(items[0]);
            if (elementType.IsArray())
                throw new ArgumentException("A qualifier value cannot be an array of arrays.", nameof(value));

            if (items.Any(item => item is null || items[0]!.GetType() != item.GetType()))
                throw new ArgumentException(
                    "The elements of a qualifier array must all have the same type and none can be null.",
                    nameof(value));

            return (elementType.AsArray(), items);
        }

        var type = value switch
        {
            string => WbemType.String,
            bool => WbemType.Boolean,
            sbyte => WbemType.Sint8,
            byte => WbemType.Uint8,
            short => WbemType.Sint16,
            ushort => WbemType.Uint16,
            int => WbemType.Sint32,
            uint => WbemType.Uint32,
            long => WbemType.Sint64,
            ulong => WbemType.Uint64,
            float => WbemType.Real32,
            double => WbemType.Real64,
            _ => throw new ArgumentException($"A qualifier value of type {value.GetType().Name} is not supported.",
                nameof(value)),
        };
        return (type, value);
    }

    /// <summary>
    /// Builds a CIM instance ObjectBlock from this instance's current values, for <c>PutInstance</c> or <c>ExecMethod</c>: each
    /// value converted to its declared type (an <see cref="int"/> to a <c>uint32</c>, a <see cref="WmiInstance"/> to an
    /// embedded object, ...).
    /// </summary>
    /// <exception cref="ArgumentException">A value cannot be converted to its property's type.</exception>
    internal WmioObjectBlock ToInstanceBlock()
    {
        var wire = RequireWireClass();
        var values = new Dictionary<string, object?>();
        foreach (var property in wire.Properties)
            if (_properties[property.Name] is { } held)
                try
                {
                    values[property.Name] = WmiConnection.CoerceValue(held.Value, property.Type);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    throw new ArgumentException(
                        $"'{ClassName}.{property.Name}' is a {WmiTypeMapping.ToWmiType(property.Type)}; its value cannot be converted to one: {ex.Message}",
                        ex);
                }

        return WmioObjectBlock.ForInstance(WmioInstance.Create(
            wire,
            values,
            _qualifiers,
            _propertyQualifiers.ToDictionary(p => p.Key, IReadOnlyList<WmioQualifier> (p) => p.Value)));
    }

    /// <inheritdoc/>
    public override string ToString() => SystemProperties.RelativePath ?? ClassName;
}
