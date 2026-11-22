namespace NWmi;

/// <summary>
/// One property of a <see cref="WmiInstance"/>: its current value, CIM type and flags, and whether the value has been set
/// since the instance was read.
/// </summary>
public sealed class WmiProperty
{
    private object? _value;

    internal WmiProperty(string name, object? value, WmiType type, WmiFlags declaredFlags, bool isValueModified)
    {
        Name = name;
        _value = value;
        Type = type;
        DeclaredFlags = declaredFlags & ~(WmiFlags.NullValue | WmiFlags.NotModified);
        IsValueModified = isValueModified;
    }

    /// <summary>
    /// A property for an instance built on the client (<see cref="WmiInstance(string, string?)"/>), its type taken from
    /// <paramref name="value"/>: a string, bool, number, char, <see cref="DateTime"/> / <see cref="DateTimeOffset"/> /
    /// <see cref="TimeSpan"/> (a datetime), <see cref="WmiInstance"/> (an embedded object, or a reference when it is the
    /// value of another reference), or an array or other sequence of one of those.
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

        _ = WmiTypeMapping.ToWbemType(type); // throws for Unknown and for a value that is not a member

        return new WmiProperty(name, Normalize(value, type), type, flags, isValueModified: true);
    }

    /// <summary>The property's name, spelled as the class declares it.</summary>
    public string Name { get; }

    /// <summary>
    /// The value, read as:
    /// <list type="bullet">
    /// <item>a datetime: a <see cref="DateTime"/> in UTC for a timestamp, a <see cref="TimeSpan"/> for an interval, and the
    /// DMTF string (<c>2026****093000.000000+060</c>) for one with wildcards;</item>
    /// <item>a reference: a <see cref="WmiInstance"/> built on the client from the path, holding the class, namespace,
    /// server and key values of the instance it refers to;</item>
    /// <item>an embedded object: a <see cref="WmiInstance"/>;</item>
    /// <item>an array: an array of the element type (<c>string[]</c>, <c>uint[]</c>, <c>DateTime[]</c>,
    /// <c>WmiInstance[]</c>, ...);</item>
    /// <item>anything else: <see cref="bool"/>, <see cref="char"/>, <see cref="string"/> or the number type of its
    /// size and sign (<see cref="byte"/>, <see cref="sbyte"/>, <see cref="ushort"/>, ..., <see cref="double"/>).</item>
    /// </list>
    /// Setting it sets <see cref="IsValueModified"/>. A datetime can be set as a <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/>, <see cref="TimeSpan"/> or DMTF string, and a reference as a <see cref="WmiInstance"/> or
    /// path string; both are stored in the form they are read in. Other values are converted to the declared type when the
    /// instance is sent.
    /// </summary>
    public object? Value
    {
        get => _value;
        set
        {
            _value = Normalize(value, Type);
            IsValueModified = true;
        }
    }

    /// <summary>
    /// The declared CIM type. Over WinRM it comes from the class definition the server gives, and is
    /// <see cref="WmiType.Unknown"/> for a class whose definition it does not give.
    /// </summary>
    public WmiType Type { get; private set; }

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

    /// <summary>Gives a value just read a more exact type, such as the declared one of a reference's key; not a modification.</summary>
    internal void Retype(WmiType type, object? value)
    {
        Type = type;
        _value = value;
    }

    internal WmiProperty Copy() =>
        new(Name, WmiInstance.CopyValue(_value), Type, DeclaredFlags, IsValueModified);

    /// <summary>A datetime or reference set in another accepted form (a <see cref="DateTimeOffset"/>, a DMTF or path string) is stored as it reads.</summary>
    private static object? Normalize(object? value, WmiType type) =>
        WmiTypeMapping.ElementType(type) is WmiType.DateTime or WmiType.Reference ? WmiValues.ToPublic(value, type) : value;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Type}) = {_value}";
}
