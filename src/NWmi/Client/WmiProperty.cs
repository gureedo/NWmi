using NWmi.Wmio;

namespace NWmi.Client;

/// <summary>
/// One property of a <see cref="WmiInstance"/>: its current value, CIM type and flags, and whether the value has been set
/// since the instance was read.
/// </summary>
public sealed class WmiProperty
{
    private object? _value;

    internal WmiProperty(string name, object? value, WmiType type, WmiFlags declaredFlags, WbemType? wireType,
        bool isValueModified)
    {
        Name = name;
        _value = value;
        Type = type;
        DeclaredFlags = declaredFlags & ~(WmiFlags.NullValue | WmiFlags.NotModified);
        WireType = wireType;
        IsValueModified = isValueModified;
    }

    /// <summary>
    /// A property for an instance built on the client (<see cref="WmiInstance(string, string?)"/>), its type taken from
    /// <paramref name="value"/>: a string, bool, number, char, <see cref="DateTimeOffset"/> / <see cref="TimeSpan"/>
    /// (a datetime), <see cref="WmiInstance"/>, or an array or other sequence of one of those.
    /// </summary>
    /// <param name="name">The property's name.</param>
    /// <param name="value">The value; null makes a <see cref="WmiType.String"/>.</param>
    /// <param name="flags">Its flags, such as <see cref="WmiFlags.Key"/>.</param>
    /// <exception cref="NotSupportedException">The type of <paramref name="value"/> has no CIM type.</exception>
    public static WmiProperty Create(string name, object? value, WmiFlags flags) =>
        Create(name, value, WmiTypeMapping.ToWmiType(WmiConnection.InferCimType(value)), flags);

    /// <summary>A property for an instance built on the client, of the CIM type <paramref name="type"/>.</summary>
    /// <param name="name">The property's name.</param>
    /// <param name="value">The value.</param>
    /// <param name="type">The CIM type.</param>
    /// <param name="flags">Its flags, such as <see cref="WmiFlags.Key"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or a system property name (<c>__CLASS</c>, ...), or <paramref name="type"/> is <see cref="WmiType.Unknown"/>.</exception>
    public static WmiProperty Create(string name, object? value, WmiType type, WmiFlags flags)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.StartsWith("__", StringComparison.Ordinal))
            throw new ArgumentException($"'{name}' is a system property name.", nameof(name));

        var wireType = WmiTypeMapping.ToWbemType(type);
        return new WmiProperty(name, Coerce(value, wireType), type, flags, wireType, isValueModified: true);
    }

    /// <summary>The property's name, spelled as the class declares it.</summary>
    public string Name { get; }

    /// <summary>
    /// The value. A datetime is a DMTF string (<c>20261121093000.000000+060</c>); setting a <see cref="DateTimeOffset"/>
    /// or <see cref="TimeSpan"/> stores one. An embedded object is a <see cref="WmiInstance"/>, an array an
    /// <c>object?[]</c>. Setting it sets <see cref="IsValueModified"/>.
    /// </summary>
    public object? Value
    {
        get => _value;
        set
        {
            _value = Coerce(value, WireType);
            IsValueModified = true;
        }
    }

    /// <summary>
    /// The declared CIM type. Over WinRM it comes from the class definition the server gives, and is
    /// <see cref="WmiType.Unknown"/> for a class whose definition it does not give.
    /// </summary>
    public WmiType Type { get; }

    /// <summary>
    /// <see cref="WmiFlags.Property"/> (or <see cref="WmiFlags.Parameter"/> for a method's parameters), plus
    /// <see cref="WmiFlags.Key"/>, <see cref="WmiFlags.Required"/>, <see cref="WmiFlags.ReadOnly"/>, <see cref="WmiFlags.In"/>,
    /// <see cref="WmiFlags.Out"/> from the class's qualifiers; <see cref="WmiFlags.NullValue"/> while the value is null, and
    /// <see cref="WmiFlags.NotModified"/> while <see cref="IsValueModified"/> is false. A property made with
    /// <see cref="Create(string, object?, WmiType, WmiFlags)"/> has the flags it was given.
    /// </summary>
    public WmiFlags Flags =>
        DeclaredFlags | (_value is null ? WmiFlags.NullValue : 0) | (IsValueModified ? 0 : WmiFlags.NotModified);

    /// <summary>Whether the value has been set since the instance was read; true for a property made on the client.</summary>
    public bool IsValueModified { get; set; }

    internal WmiFlags DeclaredFlags { get; }

    /// <summary>The wire type the value is encoded as, when known.</summary>
    internal WbemType? WireType { get; }

    internal WmiProperty Copy() =>
        new(Name, WmiInstance.CopyValue(_value), Type, DeclaredFlags, WireType, IsValueModified);

    /// <summary>A datetime is a DMTF string on the wire and when read, so a <see cref="DateTimeOffset"/> or <see cref="TimeSpan"/> becomes one.</summary>
    private static object? Coerce(object? value, WbemType? wireType) =>
        wireType?.BaseType() == WbemType.DateTime ? WmiConnection.CoerceValue(value, wireType.Value) : value;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Type}) = {_value}";
}
