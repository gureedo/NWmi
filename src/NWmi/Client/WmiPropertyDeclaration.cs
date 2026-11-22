namespace NWmi.Client;

/// <summary>A property as its class declares it: type, flags, qualifiers and default value.</summary>
public sealed class WmiPropertyDeclaration
{
    internal WmiPropertyDeclaration(string name, WmiType type, WmiFlags flags,
        WmiReadOnlyKeyedCollection<WmiQualifier> qualifiers, object? value, string? referenceClassName)
    {
        Name = name;
        Type = type;
        Flags = flags;
        Qualifiers = qualifiers;
        Value = value;
        ReferenceClassName = referenceClassName;
    }

    /// <summary>The property's name.</summary>
    public string Name { get; }

    /// <summary>The CIM type; <see cref="WmiType.Unknown"/> for a WinRM class the server does not describe.</summary>
    public WmiType Type { get; }

    /// <summary>
    /// <see cref="WmiFlags.Property"/> plus <see cref="WmiFlags.Key"/>, <see cref="WmiFlags.Required"/> and
    /// <see cref="WmiFlags.ReadOnly"/> (<c>read</c> without <c>write</c>) from the qualifiers, and
    /// <see cref="WmiFlags.NullValue"/> when there is no default value.
    /// </summary>
    public WmiFlags Flags { get; }

    /// <summary>The property's qualifiers (the <c>CIMTYPE</c> qualifier is <see cref="Type"/> and <see cref="ReferenceClassName"/>, and not listed).</summary>
    public WmiReadOnlyKeyedCollection<WmiQualifier> Qualifiers { get; }

    /// <summary>The default value, or null.</summary>
    public object? Value { get; }

    /// <summary>For a reference, the class it refers to (<c>Win32_Account</c> for <c>Win32_LoggedOnUser.Antecedent</c>), when the class says.</summary>
    public string? ReferenceClassName { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Type})";
}
