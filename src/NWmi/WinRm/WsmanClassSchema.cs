using System.Globalization;
using System.Xml.Linq;
using NWmi.Client;

namespace NWmi.WinRm;

/// <summary>A property's CIM type name (<c>uint32</c>, <c>string</c>, ...) and whether it is an array.</summary>
internal readonly record struct WsmanPropertyType(string CimType, bool IsArray);

/// <summary>
/// A class definition as a listener returns it for a class Get or a class enumeration (CIM-XML <c>CLASS</c>, DSP0201): the
/// <see cref="WmiClass"/> it describes, and the wire types of its properties and of its methods' parameters, since a
/// WS-Management instance is untyped text.
/// </summary>
internal sealed class WsmanClassSchema
{
    private readonly Dictionary<string, WsmanPropertyType> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WsmanClassSchema> _methods = new(StringComparer.OrdinalIgnoreCase);

    private WsmanClassSchema(WmiClass @class) => Class = @class;

    /// <summary>The class. For a method's pseudo class, a <c>__PARAMETERS</c> class of <c>ReturnValue</c> and the output parameters.</summary>
    public WmiClass Class { get; }

    public bool TryGetProperty(string name, out WsmanPropertyType type) => _properties.TryGetValue(name, out type);

    /// <summary>The output of a method as a pseudo class: <c>ReturnValue</c> plus the parameters.</summary>
    public WsmanClassSchema? Method(string name) => _methods.GetValueOrDefault(name);

    /// <summary>Reads a <c>CLASS</c> element.</summary>
    public static WsmanClassSchema Parse(XElement cls, string? server = null, string? @namespace = null)
    {
        var properties = new List<WmiPropertyDeclaration>();
        var methods = new List<WmiMethodDeclaration>();
        var methodSchemas = new Dictionary<string, WsmanClassSchema>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, WsmanPropertyType>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in cls.Elements())
            switch (element.Name.LocalName)
            {
                case "PROPERTY" or "PROPERTY.ARRAY" or "PROPERTY.REFERENCE":
                    if (Add(types, element) is { } name)
                    {
                        var qualifiers = Qualifiers(element);
                        var flags = DeclarationFlags(qualifiers, isParameter: false);
                        var value = DefaultValue(element);
                        properties.Add(new WmiPropertyDeclaration(name, TypeOf(element, qualifiers),
                            value is null ? flags | WmiFlags.NullValue : flags, Collection(qualifiers), value,
                            (string?)element.Attribute("REFERENCECLASS")));
                    }

                    break;
                case "METHOD":
                    var (declaration, schema) = ParseMethod(element, server, @namespace);
                    methods.Add(declaration);
                    methodSchemas[declaration.Name] = schema;
                    break;
            }

        var classQualifiers = Qualifiers(cls);
        var @class = new WmiClass((string?)cls.Attribute("NAME") ?? string.Empty, (string?)cls.Attribute("SUPERCLASS"),
            () => properties, () => methods, () => classQualifiers, server, @namespace);
        var result = new WsmanClassSchema(@class);
        foreach (var (name, type) in types)
            result._properties[name] = type;
        foreach (var (name, schema) in methodSchemas)
            result._methods[name] = schema;

        return result;
    }

    private static (WmiMethodDeclaration Declaration, WsmanClassSchema Schema) ParseMethod(XElement method,
        string? server, string? @namespace)
    {
        var name = (string?)method.Attribute("NAME") ?? string.Empty;
        var types = new Dictionary<string, WsmanPropertyType>(StringComparer.OrdinalIgnoreCase);
        var returnTypeName = (string?)method.Attribute("TYPE");
        if (returnTypeName is not null)
            types["ReturnValue"] = new WsmanPropertyType(returnTypeName, false);

        var parameters = new List<WmiMethodParameterDeclaration>();
        foreach (var parameter in method.Elements()
                     .Where(e => e.Name.LocalName.StartsWith("PARAMETER", StringComparison.Ordinal)))
            if (Add(types, parameter) is { } parameterName)
            {
                var qualifiers = Qualifiers(parameter);
                parameters.Add(new WmiMethodParameterDeclaration(parameterName, TypeOf(parameter, qualifiers),
                    DeclarationFlags(qualifiers, isParameter: true), Collection(qualifiers),
                    (string?)parameter.Attribute("REFERENCECLASS")));
            }

        var ordered = parameters
            .OrderBy(p => p.Qualifiers["ID"]?.Value is int id ? id : int.MaxValue)
            .ToList();
        var returnType = returnTypeName is null ? WmiType.Unknown : WmiTypeMapping.FromCimXml(returnTypeName, false);
        var declaration = new WmiMethodDeclaration(name, returnType,
            new WmiReadOnlyKeyedCollection<WmiMethodParameterDeclaration>(ordered, p => p.Name),
            Collection(Qualifiers(method)));

        var output = ordered
            .Where(p => (p.Flags & WmiFlags.Out) != 0)
            .Select(p => new WmiPropertyDeclaration(p.Name, p.Type, p.Flags | WmiFlags.NullValue, p.Qualifiers, null,
                p.ReferenceClassName))
            .Prepend(new WmiPropertyDeclaration("ReturnValue", returnType,
                WmiFlags.Parameter | WmiFlags.Out | WmiFlags.NullValue, Collection([]), null, null))
            .ToList();
        var schema = new WsmanClassSchema(WmiClass.Synthesized("__PARAMETERS", output, server, @namespace));
        foreach (var (key, type) in types)
            schema._properties[key] = type;

        return (declaration, schema);
    }

    /// <summary>Records the wire type of a property or parameter; returns its name, or null when it has none.</summary>
    private static string? Add(Dictionary<string, WsmanPropertyType> types, XElement element)
    {
        var name = (string?)element.Attribute("NAME");
        if (name is null)
            return null;

        var local = element.Name.LocalName;
        var isReference = local.Contains("REF", StringComparison.Ordinal);
        var type = isReference ? "reference" : (string?)element.Attribute("TYPE") ?? "string";
        types[name] = new WsmanPropertyType(type, local.EndsWith("ARRAY", StringComparison.Ordinal));
        return name;
    }

    /// <summary>
    /// The CIM type of a property or parameter. CIM-XML has no type for an embedded object: it is a <c>string</c> with an
    /// <c>EmbeddedInstance</c> or <c>EmbeddedObject</c> qualifier.
    /// </summary>
    private static WmiType TypeOf(XElement element, List<WmiQualifier> qualifiers)
    {
        var local = element.Name.LocalName;
        var isArray = local.EndsWith("ARRAY", StringComparison.Ordinal);
        if (local.Contains("REF", StringComparison.Ordinal))
            return isArray ? WmiType.ReferenceArray : WmiType.Reference;

        var embedded = qualifiers.Any(q =>
            (string.Equals(q.Name, "EmbeddedInstance", StringComparison.OrdinalIgnoreCase) && q.Value is string) ||
            (string.Equals(q.Name, "EmbeddedObject", StringComparison.OrdinalIgnoreCase) && q.Value is true));
        if (embedded)
            return isArray ? WmiType.InstanceArray : WmiType.Instance;

        return WmiTypeMapping.FromCimXml((string?)element.Attribute("TYPE") ?? "string", isArray);
    }

    private static List<WmiQualifier> Qualifiers(XElement element) =>
    [
        .. element.Elements()
            .Where(e => e.Name.LocalName == "QUALIFIER")
            .Select(q =>
            {
                var typeName = (string?)q.Attribute("TYPE") ?? "string";
                var flags = IsFalse(q, "OVERRIDABLE") ? WmiFlags.DisableOverride : WmiFlags.EnableOverride;
                flags |= IsFalse(q, "TOSUBCLASS") ? WmiFlags.Restricted : WmiFlags.ToSubclass;
                if (string.Equals((string?)q.Attribute("TRANSLATABLE"), "true", StringComparison.OrdinalIgnoreCase))
                    flags |= WmiFlags.Translatable;

                var array = q.Element("VALUE.ARRAY");
                return new WmiQualifier((string?)q.Attribute("NAME") ?? string.Empty,
                    array is not null
                        ? array.Elements("VALUE").Select(v => (object?)ConvertValue(v.Value, typeName)).ToArray()
                        : q.Element("VALUE") is { } single
                            ? ConvertValue(single.Value, typeName)
                            : null,
                    WmiTypeMapping.FromCimXml(typeName, array is not null), flags);
            }),
    ];

    private static bool IsFalse(XElement element, string attribute) =>
        string.Equals((string?)element.Attribute(attribute), "false", StringComparison.OrdinalIgnoreCase);

    private static WmiReadOnlyKeyedCollection<WmiQualifier> Collection(IEnumerable<WmiQualifier> qualifiers) =>
        new(qualifiers, q => q.Name);

    /// <summary>The same flags as for a class read over DCOM, from the same qualifiers.</summary>
    private static WmiFlags DeclarationFlags(List<WmiQualifier> qualifiers, bool isParameter)
    {
        var flags = isParameter ? WmiFlags.Parameter : WmiFlags.Property;
        if (IsSet("key"))
            flags |= WmiFlags.Key;
        if (IsSet("required"))
            flags |= WmiFlags.Required;
        if (IsSet("read") && !IsSet("write"))
            flags |= WmiFlags.ReadOnly;
        if (isParameter && IsSet("in"))
            flags |= WmiFlags.In;
        if (isParameter && IsSet("out"))
            flags |= WmiFlags.Out;

        return flags;

        bool IsSet(string name) =>
            qualifiers.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase) && q.Value is true);
    }

    /// <summary>A property's default value (<c>VALUE</c> or <c>VALUE.ARRAY</c>), or null.</summary>
    private static object? DefaultValue(XElement property)
    {
        var typeName = (string?)property.Attribute("TYPE") ?? "string";
        if (property.Element("VALUE.ARRAY") is { } array)
            return array.Elements("VALUE").Select(object? (v) => ConvertValue(v.Value, typeName)).ToArray();

        return property.Element("VALUE") is { } value ? ConvertValue(value.Value, typeName) : null;
    }

    /// <summary>A CIM-XML value: as <see cref="Convert"/>, but CIM-XML spells booleans <c>TRUE</c> and <c>FALSE</c>.</summary>
    private static object ConvertValue(string text, string cimType) =>
        cimType == "boolean" && bool.TryParse(text, out var flag) ? flag : Convert(text, cimType);

    /// <summary>The CLR value of <paramref name="text"/> for a CIM scalar type, or the text itself when it does not parse.</summary>
    public static object Convert(string text, string cimType)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (cimType)
        {
            case "boolean":
                return text switch { "true" or "1" => true, "false" or "0" => false, _ => text };
            case "uint8" when byte.TryParse(text, NumberStyles.Integer, culture, out var u8):
                return u8;
            case "sint8" when sbyte.TryParse(text, NumberStyles.Integer, culture, out var s8):
                return s8;
            case "uint16" when ushort.TryParse(text, NumberStyles.Integer, culture, out var u16):
                return u16;
            case "sint16" when short.TryParse(text, NumberStyles.Integer, culture, out var s16):
                return s16;
            case "uint32" when uint.TryParse(text, NumberStyles.Integer, culture, out var u32):
                return u32;
            case "sint32" when int.TryParse(text, NumberStyles.Integer, culture, out var s32):
                return s32;
            case "uint64" when ulong.TryParse(text, NumberStyles.Integer, culture, out var u64):
                return u64;
            case "sint64" when long.TryParse(text, NumberStyles.Integer, culture, out var s64):
                return s64;
            case "real32" when float.TryParse(text, NumberStyles.Float, culture, out var r32):
                return r32;
            case "real64" when double.TryParse(text, NumberStyles.Float, culture, out var r64):
                return r64;
            case "char16" when text.Length == 1:
                return text[0];
            default:
                return text;
        }
    }
}
