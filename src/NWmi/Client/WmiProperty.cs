namespace NWmi.Client;

/// <summary>One property of a <see cref="WmiObject"/>: its value, CIM type and flags, as they were when it was read.</summary>
public sealed class WmiProperty
{
    internal WmiProperty(string name, object? value, WmiType type, WmiFlags flags)
    {
        Name = name;
        Value = value;
        Type = type;
        Flags = flags;
    }

    /// <summary>The property's name, spelled as the class declares it.</summary>
    public string Name { get; }

    /// <summary>The value, as <see cref="WmiObject"/>'s indexer returns it.</summary>
    public object? Value { get; }

    /// <summary>
    /// The declared CIM type. Over WinRM it comes from the class definition the server gives, and is
    /// <see cref="WmiType.Unknown"/> for a class whose definition it does not give.
    /// </summary>
    public WmiType Type { get; }

    /// <summary>
    /// <see cref="WmiFlags.Property"/> (or <see cref="WmiFlags.Parameter"/> for a method's parameters), plus
    /// <see cref="WmiFlags.Key"/>, <see cref="WmiFlags.Required"/>, <see cref="WmiFlags.ReadOnly"/>, <see cref="WmiFlags.In"/>,
    /// <see cref="WmiFlags.Out"/> from the class's qualifiers, <see cref="WmiFlags.NullValue"/> when the value is null, and
    /// <see cref="WmiFlags.NotModified"/> on an instance whose value has not been set since it was read. Over WinRM the class's
    /// qualifiers are not read, so only <see cref="WmiFlags.Key"/> is known among them.
    /// </summary>
    public WmiFlags Flags { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Type}) = {Value}";
}
