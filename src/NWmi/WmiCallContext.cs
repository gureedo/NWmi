using NWmi.Options;
using NWmi.Wmi;

namespace NWmi;

/// <summary>
/// Named values sent with each WMI call (an <c>IWbemContext</c>, MS-WMI 2.2.13) for the server or a provider to read.
/// The best-known use is choosing the registry view of <c>StdRegProv</c>: <c>__ProviderArchitecture</c> 32 or 64 (and
/// <c>__RequiredArchitecture</c> true). Values are string, int or bool. A context is immutable; <c>With</c> returns a
/// changed copy. Set one on <see cref="WmiConnectionOptions.Context"/> or <see cref="WmiConnection.Context"/>.
/// </summary>
/// <example>
/// <code>
/// wmi.Context = WmiCallContext.Empty.With("__ProviderArchitecture", 64).With("__RequiredArchitecture", true);
/// </code>
/// </example>
public sealed class WmiCallContext
{
    private readonly KeyValuePair<string, object>[] _values;

    private WmiCallContext(KeyValuePair<string, object>[] values) => _values = values;

    /// <summary>A context with no values; the same as passing none.</summary>
    public static WmiCallContext Empty { get; } = new([]);

    /// <summary>The values, in the order they were added.</summary>
    public IReadOnlyDictionary<string, object> Values => new Dictionary<string, object>(_values, StringComparer.Ordinal);

    /// <summary>True when there are no values.</summary>
    public bool IsEmpty => _values.Length == 0;

    /// <summary>Returns a copy with <paramref name="name"/> set to a string, replacing an earlier value of that name.</summary>
    public WmiCallContext With(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(name, value);
    }

    /// <summary>Returns a copy with <paramref name="name"/> set to an integer, replacing an earlier value of that name.</summary>
    public WmiCallContext With(string name, int value) => Add(name, value);

    /// <summary>Returns a copy with <paramref name="name"/> set to a boolean, replacing an earlier value of that name.</summary>
    public WmiCallContext With(string name, bool value) => Add(name, value);

    private WmiCallContext Add(string name, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var values = _values.Where(v => v.Key != name).Append(KeyValuePair.Create(name, value)).ToArray();
        return new WmiCallContext(values);
    }

    /// <summary>The wire form, or null for an empty context (which is how an absent one is sent).</summary>
    internal WmiContext? ToWire()
    {
        if (IsEmpty)
            return null;

        var wire = WmiContext.Empty;
        foreach (var (name, value) in _values)
            _ = value switch
            {
                int i => wire.SetInt32(name, i),
                bool b => wire.SetBoolean(name, b),
                _ => wire.SetString(name, (string)value),
            };

        return wire;
    }
}
