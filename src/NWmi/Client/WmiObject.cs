using NWmi.Wmi;
using NWmi.Wmio;

namespace NWmi.Client;

/// <summary>
/// A CIM class or instance, as returned by <see cref="WmiConnection"/>: typed property access plus the system
/// properties (<c>__CLASS</c>, <c>__PATH</c>, <c>__RELPATH</c>, <c>__NAMESPACE</c>, <c>__SERVER</c>) real WMI
/// clients expect, synthesized from the class's schema and the connection's own namespace/server rather than
/// stored as ordinary properties (they are not part of the WMIO value table).
/// </summary>
/// <remarks>
/// Wraps a <see cref="WmioClass"/> (the schema) and a mutable copy of its property values: for an instance
/// (from <c>GetObject</c>, a query, or an enumeration) the values start out as what the server sent; for a
/// bare class (from <c>GetObject</c> on a class path) they start out as the class's own declared defaults, so
/// setting properties and calling <see cref="WmiConnection.PutInstanceAsync"/> with
/// <see cref="WbemChangeFlags.CreateOnly"/> creates a new instance from it.
/// </remarks>
public sealed class WmiObject
{
    private readonly WmioClass? _class;
    private readonly IReadOnlyList<string> _keyNames = [];
    private readonly Dictionary<string, object?> _values;
    private readonly List<WmioQualifier> _qualifiers = [];

    private readonly Dictionary<string, List<WmioQualifier>>
        _propertyQualifiers = new(StringComparer.OrdinalIgnoreCase);

    private readonly WmioDecoration? _decoration;
    private readonly string? _serverHint;
    private readonly string? _namespaceHint;

    internal WmiObject(WmioObjectBlock block, string? serverHint, string? namespaceHint)
    {
        ArgumentNullException.ThrowIfNull(block);
        _decoration = block.Decoration;
        _serverHint = serverHint;
        _namespaceHint = namespaceHint;
        if (block.IsClass)
        {
            _class = block.Class!;
            _values = new Dictionary<string, object?>(
                _class.Properties.ToDictionary(p => p.Name, p => p.DefaultValue), StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _class = block.Instance!.Class;
            _values = new Dictionary<string, object?>(block.Instance!.Values, StringComparer.OrdinalIgnoreCase);
            _qualifiers.AddRange(block.Instance.Qualifiers);
            foreach (var (property, set) in block.Instance.PropertyQualifiers ??
                                            new Dictionary<string, IReadOnlyList<WmioQualifier>>())
                if (set.Count > 0)
                    _propertyQualifiers[property] = [.. set];
        }
    }

    /// <summary>
    /// An object that did not come from the binary object format, i.e. one read from a WS-Management reply, which carries
    /// no class metadata: <paramref name="keyNames"/> are the key properties as the reply's selectors name them.
    /// </summary>
    internal WmiObject(string className, IEnumerable<KeyValuePair<string, object?>> values,
        IReadOnlyList<string> keyNames, string? serverHint, string? namespaceHint)
    {
        ClassName = className;
        _values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
        _keyNames = keyNames;
        _serverHint = serverHint;
        _namespaceHint = namespaceHint;
    }

    /// <summary>The class this object belongs to (<c>__CLASS</c>).</summary>
    public string ClassName => _class?.Name ?? field ?? string.Empty;

    /// <summary>The CIM server this object came from (<c>__SERVER</c>), from the wire decoration if present, otherwise the connection's host.</summary>
    public string? Server => _decoration?.Server ?? _serverHint;

    /// <summary>The namespace this object lives in (<c>__NAMESPACE</c>), from the wire decoration if present, otherwise the connection's namespace.</summary>
    public string? Namespace => _decoration?.Namespace ?? _namespaceHint;

    /// <summary>The names of this class's key properties, in declaration order (MS-WMIO's <c>key</c> qualifier).</summary>
    public IReadOnlyList<string> KeyPropertyNames => KeyNames();

    /// <summary>
    /// <c>ClassName.Key1=value1,...</c> (<c>__RELPATH</c>). An object with no key properties (an instance of a keyless
    /// class), or whose key properties have no value yet (a bare class object, which declares keys but holds none), has no
    /// instance-key form, so this is just <see cref="ClassName"/>.
    /// </summary>
    public string RelativePath => BuildPath(null, null).RelativePath;

    /// <summary><c>\\server\namespace:RelativePath</c> (<c>__PATH</c>); null when this object has no known namespace.</summary>
    public string? Path => Namespace is null ? null : BuildPath(Namespace, Server).ToString();

    /// <summary>The path of this object, parsed (<see cref="Path"/> when it has a namespace, otherwise <see cref="RelativePath"/>).</summary>
    public CimObjectPath ObjectPath => CimObjectPath.Parse(Path ?? RelativePath);

    /// <summary>The key values go into the path only for an object that holds a value for every key property.</summary>
    private CimObjectPath BuildPath(string? @namespace, string? server)
    {
        var keys = KeyNames();
        var hasKeys = keys.Count > 0 && keys.All(k => _values.GetValueOrDefault(k) is not null);
        return new CimObjectPath(ClassName,
            hasKeys ? keys.Select(k => new KeyValuePair<string, object>(k, _values[k]!)) : null, @namespace, server);
    }

    /// <summary>Every ordinary (non-system) property and its current value.</summary>
    public IReadOnlyDictionary<string, object?> Properties => _values;

    /// <summary>Gets a property value, by a name matched case-insensitively (an embedded object comes back as a <see cref="WmiObject"/>), or one of the synthesized system properties (<c>__CLASS</c>, <c>__PATH</c>, <c>__RELPATH</c>, <c>__NAMESPACE</c>, <c>__SERVER</c>).</summary>
    /// <exception cref="KeyNotFoundException">Neither a system property nor a property of <see cref="ClassName"/>.</exception>
    public object? this[string name]
    {
        get => name switch
        {
            "__CLASS" => ClassName,
            "__PATH" => Path,
            "__RELPATH" => RelativePath,
            "__NAMESPACE" => Namespace,
            "__SERVER" => Server,
            _ => _values.TryGetValue(name, out var value)
                ? WrapEmbedded(value)
                : throw new KeyNotFoundException($"'{ClassName}' has no property named '{name}'."),
        };
        set
        {
            if (name.StartsWith("__", StringComparison.Ordinal))
                throw new ArgumentException($"'{name}' is a system property and cannot be set.", nameof(name));

            var property = _class?.FindProperty(name);
            if (_class is null ? !_values.ContainsKey(name) : property is null)
                throw new ArgumentException($"'{ClassName}' has no property named '{name}'.", nameof(name));

            // A datetime is a DMTF string on the wire and when read, so a DateTimeOffset or TimeSpan becomes one here.
            _values[name] = property?.Type.BaseType() == CimType.DateTime
                ? WmiConnection.CoerceValue(value, property.Type)
                : value;
        }
    }

    /// <summary>
    /// The qualifiers set on one property of this instance (not the ones its class declares, such as <c>key</c>); property and
    /// qualifier names match case-insensitively. Empty when there are none. An instance read from the server keeps the ones it came
    /// with, and <see cref="WmiConnection.PutInstanceAsync"/> sends them back.
    /// </summary>
    /// <exception cref="ArgumentException">The object has no such property.</exception>
    /// <exception cref="InvalidOperationException">The object was read over WinRM, which carries no qualifiers.</exception>
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
    /// <exception cref="ArgumentException">The object has no such property, the name is empty, or the value is null, an empty array, or of another type (to remove a qualifier use <see cref="RemovePropertyQualifier"/>).</exception>
    /// <exception cref="InvalidOperationException">The object was read over WinRM.</exception>
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
    /// <exception cref="ArgumentException">The object has no such property.</exception>
    /// <exception cref="InvalidOperationException">The object was read over WinRM.</exception>
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

    private WmioClass RequireClass() =>
        _class ?? throw new InvalidOperationException(
            "This object was read over WinRM and has no binary class definition.");

    private WmioProperty RequireProperty(string propertyName) =>
        RequireClass().FindProperty(propertyName)
        ?? throw new ArgumentException($"'{ClassName}' has no property named '{propertyName}'.", nameof(propertyName));

    private static (CimType Type, object Value) InferQualifierValue(object? value)
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
            string => CimType.String,
            bool => CimType.Boolean,
            sbyte => CimType.Sint8,
            byte => CimType.Uint8,
            short => CimType.Sint16,
            ushort => CimType.Uint16,
            int => CimType.Sint32,
            uint => CimType.Uint32,
            long => CimType.Sint64,
            ulong => CimType.Uint64,
            float => CimType.Real32,
            double => CimType.Real64,
            _ => throw new ArgumentException($"A qualifier value of type {value.GetType().Name} is not supported.",
                nameof(value)),
        };
        return (type, value);
    }

    private object? WrapEmbedded(object? value) => value switch
    {
        WmioObjectBlock embedded => new WmiObject(embedded, _serverHint, _namespaceHint),
        object?[] array => array.Select(WrapEmbedded).ToArray(),
        _ => value,
    };

    /// <summary>Builds a CIM instance ObjectBlock from this object's current values, for <c>PutInstance</c> or <c>ExecMethod</c>.</summary>
    internal WmioObjectBlock ToInstanceBlock() =>
        WmioObjectBlock.ForInstance(WmioInstance.Create(
            RequireClass(),
            _values,
            _qualifiers,
            _propertyQualifiers.ToDictionary(p => p.Key, p => (IReadOnlyList<WmioQualifier>)p.Value)));

    private List<string> KeyNames() => _class is null ? [.. _keyNames] : [.. KeyProperties().Select(p => p.Name)];

    private List<WmioProperty> KeyProperties() =>
        _class is null
            ? []
            :
            [
                .. _class.Properties.Where(p => p.Qualifiers.Any(q =>
                    string.Equals(q.Name, "key", StringComparison.OrdinalIgnoreCase) && q.Value is true))
            ];
}
