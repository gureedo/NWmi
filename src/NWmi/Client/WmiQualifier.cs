namespace NWmi.Client;

/// <summary>A qualifier of a class, property, method or parameter, as the class declares it.</summary>
public sealed class WmiQualifier
{
    internal WmiQualifier(string name, object? value, WmiType type, WmiFlags flags)
    {
        Name = name;
        Value = value;
        Type = type;
        Flags = flags;
    }

    /// <summary>The qualifier's name (<c>key</c>, <c>read</c>, <c>MaxLen</c>, ...).</summary>
    public string Name { get; }

    /// <summary>The value: a <see cref="bool"/>, a number, a string, or an <c>object?[]</c> of them.</summary>
    public object? Value { get; }

    /// <summary>The value's CIM type.</summary>
    public WmiType Type { get; }

    /// <summary>
    /// The flavor: <see cref="WmiFlags.EnableOverride"/> or <see cref="WmiFlags.DisableOverride"/>,
    /// <see cref="WmiFlags.ToSubclass"/> or <see cref="WmiFlags.Restricted"/>, and <see cref="WmiFlags.Translatable"/> for an
    /// amended (localized) qualifier.
    /// </summary>
    public WmiFlags Flags { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} = {Value}";
}
