using NWmi.Wmio;

namespace NWmi.Client;

/// <summary>Converts between the public <see cref="WmiType"/> / <see cref="WmiFlags"/> and their wire forms.</summary>
internal static class WmiTypeMapping
{
    private const int ArrayOffset = WmiType.BooleanArray - WmiType.Boolean;

    public static WmiType ToWmiType(WbemType type)
    {
        var scalar = type.BaseType() switch
        {
            WbemType.Boolean => WmiType.Boolean,
            WbemType.Uint8 => WmiType.UInt8,
            WbemType.Sint8 => WmiType.SInt8,
            WbemType.Uint16 => WmiType.UInt16,
            WbemType.Sint16 => WmiType.SInt16,
            WbemType.Uint32 => WmiType.UInt32,
            WbemType.Sint32 => WmiType.SInt32,
            WbemType.Uint64 => WmiType.UInt64,
            WbemType.Sint64 => WmiType.SInt64,
            WbemType.Real32 => WmiType.Real32,
            WbemType.Real64 => WmiType.Real64,
            WbemType.Char16 => WmiType.Char16,
            WbemType.DateTime => WmiType.DateTime,
            WbemType.String => WmiType.String,
            WbemType.Reference => WmiType.Reference,
            WbemType.Object => WmiType.Instance,
            _ => WmiType.Unknown,
        };
        return scalar != WmiType.Unknown && type.IsArray() ? AsArray(scalar) : scalar;
    }

    /// <exception cref="ArgumentException"><paramref name="type"/> is <see cref="WmiType.Unknown"/> or not a member.</exception>
    public static WbemType ToWbemType(WmiType type)
    {
        var scalar = ElementType(type) switch
        {
            WmiType.Boolean => WbemType.Boolean,
            WmiType.UInt8 => WbemType.Uint8,
            WmiType.SInt8 => WbemType.Sint8,
            WmiType.UInt16 => WbemType.Uint16,
            WmiType.SInt16 => WbemType.Sint16,
            WmiType.UInt32 => WbemType.Uint32,
            WmiType.SInt32 => WbemType.Sint32,
            WmiType.UInt64 => WbemType.Uint64,
            WmiType.SInt64 => WbemType.Sint64,
            WmiType.Real32 => WbemType.Real32,
            WmiType.Real64 => WbemType.Real64,
            WmiType.Char16 => WbemType.Char16,
            WmiType.DateTime => WbemType.DateTime,
            WmiType.String => WbemType.String,
            WmiType.Reference => WbemType.Reference,
            WmiType.Instance => WbemType.Object,
            _ => throw new ArgumentException($"{type} has no wire type.", nameof(type)),
        };
        return IsArray(type) ? scalar.AsArray() : scalar;
    }

    /// <summary>A CIM-XML <c>TYPE</c> (<c>uint32</c>, <c>string</c>, ...), as a WinRM class definition gives it.</summary>
    public static WmiType FromCimXml(string typeName, bool isArray)
    {
        var scalar = typeName switch
        {
            "boolean" => WmiType.Boolean,
            "uint8" => WmiType.UInt8,
            "sint8" => WmiType.SInt8,
            "uint16" => WmiType.UInt16,
            "sint16" => WmiType.SInt16,
            "uint32" => WmiType.UInt32,
            "sint32" => WmiType.SInt32,
            "uint64" => WmiType.UInt64,
            "sint64" => WmiType.SInt64,
            "real32" => WmiType.Real32,
            "real64" => WmiType.Real64,
            "char16" => WmiType.Char16,
            "datetime" => WmiType.DateTime,
            "string" => WmiType.String,
            "reference" => WmiType.Reference,
            "object" => WmiType.Instance,
            _ => WmiType.Unknown,
        };
        return scalar != WmiType.Unknown && isArray ? AsArray(scalar) : scalar;
    }

    public static bool IsArray(WmiType type) => type is >= WmiType.BooleanArray and <= WmiType.InstanceArray;

    public static WmiType ElementType(WmiType type) => IsArray(type) ? type - ArrayOffset : type;

    public static WmiType AsArray(WmiType type) =>
        type is >= WmiType.Boolean and <= WmiType.Instance
            ? type + ArrayOffset
            : throw new ArgumentException($"{type} has no array type.", nameof(type));

    /// <summary>
    /// The flags of a property or parameter declared by a class: <see cref="WmiFlags.Key"/>, <see cref="WmiFlags.Required"/>,
    /// <see cref="WmiFlags.ReadOnly"/> (<c>read</c> without <c>write</c>) and, on a parameter, <see cref="WmiFlags.In"/> /
    /// <see cref="WmiFlags.Out"/>, all from its qualifiers.
    /// </summary>
    public static WmiFlags DeclarationFlags(IReadOnlyList<WmioQualifier> qualifiers, bool isParameter)
    {
        var flags = isParameter ? WmiFlags.Parameter : WmiFlags.Property;
        if (IsSet(qualifiers, "key"))
            flags |= WmiFlags.Key;
        if (IsSet(qualifiers, "required"))
            flags |= WmiFlags.Required;
        if (IsSet(qualifiers, "read") && !IsSet(qualifiers, "write"))
            flags |= WmiFlags.ReadOnly;
        if (isParameter && IsSet(qualifiers, "in"))
            flags |= WmiFlags.In;
        if (isParameter && IsSet(qualifiers, "out"))
            flags |= WmiFlags.Out;

        return flags;
    }

    /// <summary>A qualifier's flavor: whether it can be overridden, whether it propagates to subclasses, whether it is localized.</summary>
    public static WmiFlags FlavorFlags(QualifierFlavor flavor)
    {
        var flags = (flavor & QualifierFlavor.NotOverridable) != 0 ? WmiFlags.DisableOverride : WmiFlags.EnableOverride;
        flags |= (flavor & QualifierFlavor.PropagateToDerivedClass) != 0 ? WmiFlags.ToSubclass : WmiFlags.Restricted;
        if ((flavor & QualifierFlavor.Amended) != 0)
            flags |= WmiFlags.Translatable;

        return flags;
    }

    private static bool IsSet(IReadOnlyList<WmioQualifier> qualifiers, string name) =>
        qualifiers.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase) && q.Value is true);

    /// <summary>
    /// The public form of a qualifier set. <c>CIMTYPE</c> is left out: it restates the element's type (and, for a reference,
    /// the class it refers to), and a WinRM class definition does not have it.
    /// </summary>
    public static IEnumerable<WmiQualifier> Qualifiers(IEnumerable<WmioQualifier> qualifiers) =>
        qualifiers
            .Where(q => !string.Equals(q.Name, "CIMTYPE", StringComparison.OrdinalIgnoreCase))
            .Select(q => new WmiQualifier(q.Name, q.Value, ToWmiType(q.Type), FlavorFlags(q.Flavor)));

    /// <summary>The class a reference refers to, from its <c>CIMTYPE</c> qualifier (<c>ref:Win32_Account</c>); null for a plain <c>ref</c>.</summary>
    public static string? ReferenceClassName(IReadOnlyList<WmioQualifier> qualifiers)
    {
        return qualifiers
                   .FirstOrDefault(q => string.Equals(q.Name, "CIMTYPE", StringComparison.OrdinalIgnoreCase))
                   ?.Value is string cimType && cimType.StartsWith("ref:", StringComparison.OrdinalIgnoreCase) &&
               cimType.Length > 4
            ? cimType[4..]
            : null;
    }

    public static WmiPropertyDeclaration Declaration(WmioProperty property, bool isParameter, string? server,
        string? @namespace)
    {
        var flags = DeclarationFlags(property.Qualifiers, isParameter);
        if (property.DefaultValue is null)
            flags |= WmiFlags.NullValue;

        return new WmiPropertyDeclaration(property.Name, ToWmiType(property.Type), flags,
            new WmiReadOnlyKeyedCollection<WmiQualifier>(Qualifiers(property.Qualifiers), q => q.Name),
            WmiInstance.FromWireValue(property.DefaultValue, server, @namespace),
            property.Type.BaseType() == WbemType.Reference ? ReferenceClassName(property.Qualifiers) : null);
    }

    /// <summary>
    /// A method from its input and output signatures: the parameters of both, a parameter that is in both listed once,
    /// ordered by their <c>ID</c> qualifier; <c>ReturnValue</c> is the return type.
    /// </summary>
    public static WmiMethodDeclaration Method(WmioMethod method)
    {
        var input = method.DecodeInputSignature()?.Properties ?? [];
        var output = method.DecodeOutputSignature()?.Properties ?? [];
        var returnValue = output.FirstOrDefault(p =>
            string.Equals(p.Name, "ReturnValue", StringComparison.OrdinalIgnoreCase));

        var parameters = new List<(WmioProperty Property, WmiFlags Flags)>();
        foreach (var property in input.Concat(output.Where(p => p != returnValue)))
        {
            var flags = DeclarationFlags(property.Qualifiers, isParameter: true);
            var existing = parameters.FindIndex(p =>
                string.Equals(p.Property.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                parameters[existing] = parameters[existing] with { Flags = parameters[existing].Flags | flags };
            else
                parameters.Add((property, flags));
        }

        var declarations = parameters
            .OrderBy(p => p.Property.Qualifiers
                .FirstOrDefault(q => string.Equals(q.Name, "ID", StringComparison.OrdinalIgnoreCase))?.Value is int id
                ? id
                : int.MaxValue)
            .Select(p => new WmiMethodParameterDeclaration(p.Property.Name, ToWmiType(p.Property.Type), p.Flags,
                new WmiReadOnlyKeyedCollection<WmiQualifier>(Qualifiers(p.Property.Qualifiers), q => q.Name),
                p.Property.Type.BaseType() == WbemType.Reference ? ReferenceClassName(p.Property.Qualifiers) : null));

        return new WmiMethodDeclaration(method.Name,
            returnValue is null ? WmiType.Unknown : ToWmiType(returnValue.Type),
            new WmiReadOnlyKeyedCollection<WmiMethodParameterDeclaration>(declarations, p => p.Name),
            new WmiReadOnlyKeyedCollection<WmiQualifier>(Qualifiers(method.Qualifiers), q => q.Name));
    }
}
