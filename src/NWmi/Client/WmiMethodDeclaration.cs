namespace NWmi.Client;

/// <summary>A method as its class declares it: return type, parameters and qualifiers.</summary>
public sealed class WmiMethodDeclaration
{
    internal WmiMethodDeclaration(string name, WmiType returnType,
        WmiReadOnlyKeyedCollection<WmiMethodParameterDeclaration> parameters,
        WmiReadOnlyKeyedCollection<WmiQualifier> qualifiers)
    {
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
        Qualifiers = qualifiers;
    }

    /// <summary>The method's name.</summary>
    public string Name { get; }

    /// <summary>The type of <c>ReturnValue</c>; <see cref="WmiType.Unknown"/> when the method declares none.</summary>
    public WmiType ReturnType { get; }

    /// <summary>The input and output parameters in declaration order (their <c>ID</c> qualifier), without <c>ReturnValue</c>.</summary>
    public WmiReadOnlyKeyedCollection<WmiMethodParameterDeclaration> Parameters { get; }

    /// <summary>The method's qualifiers (<c>Static</c>, <c>Implemented</c>, ...).</summary>
    public WmiReadOnlyKeyedCollection<WmiQualifier> Qualifiers { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name}({string.Join(", ", Parameters.Select(p => p.Name))})";
}

/// <summary>One parameter of a <see cref="WmiMethodDeclaration"/>.</summary>
public sealed class WmiMethodParameterDeclaration
{
    internal WmiMethodParameterDeclaration(string name, WmiType type, WmiFlags flags,
        WmiReadOnlyKeyedCollection<WmiQualifier> qualifiers, string? referenceClassName)
    {
        Name = name;
        Type = type;
        Flags = flags;
        Qualifiers = qualifiers;
        ReferenceClassName = referenceClassName;
    }

    /// <summary>The parameter's name.</summary>
    public string Name { get; }

    /// <summary>The CIM type.</summary>
    public WmiType Type { get; }

    /// <summary><see cref="WmiFlags.Parameter"/> plus <see cref="WmiFlags.In"/> and / or <see cref="WmiFlags.Out"/> from the qualifiers.</summary>
    public WmiFlags Flags { get; }

    /// <summary>The parameter's qualifiers (<c>ID</c>, <c>In</c>, <c>Out</c>, ...; the <c>CIMTYPE</c> qualifier is <see cref="Type"/>, and not listed).</summary>
    public WmiReadOnlyKeyedCollection<WmiQualifier> Qualifiers { get; }

    /// <summary>For a reference, the class it refers to, when the method says.</summary>
    public string? ReferenceClassName { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Type})";
}
