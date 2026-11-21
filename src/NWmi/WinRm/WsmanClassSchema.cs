using System.Globalization;
using System.Xml.Linq;

namespace NWmi.WinRm;

/// <summary>A property's CIM type name (<c>uint32</c>, <c>string</c>, ...) and whether it is an array.</summary>
internal readonly record struct WsmanPropertyType(string CimType, bool IsArray);

/// <summary>
/// The types of a class's properties and of its methods' parameters, read from the CIM-XML class definition that a
/// listener returns for a class Get. A WS-Management instance is untyped text; this is what types it.
/// </summary>
internal sealed class WsmanClassSchema
{
    private readonly Dictionary<string, WsmanPropertyType> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WsmanClassSchema> _methods = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetProperty(string name, out WsmanPropertyType type) => _properties.TryGetValue(name, out type);

    /// <summary>The output of a method as a pseudo class: <c>ReturnValue</c> plus the parameters.</summary>
    public WsmanClassSchema? Method(string name) => _methods.GetValueOrDefault(name);

    /// <summary>Reads a <c>CLASS</c> element (qualifiers are not needed).</summary>
    public static WsmanClassSchema Parse(XElement cls)
    {
        var schema = new WsmanClassSchema();
        foreach (var element in cls.Elements())
            switch (element.Name.LocalName)
            {
                case "PROPERTY" or "PROPERTY.ARRAY" or "PROPERTY.REFERENCE":
                    schema.Add(element);
                    break;
                case "METHOD":
                    var method = new WsmanClassSchema();
                    if ((string?)element.Attribute("TYPE") is { } returnType)
                        method._properties["ReturnValue"] = new WsmanPropertyType(returnType, false);

                    foreach (var parameter in element.Elements()
                                 .Where(e => e.Name.LocalName.StartsWith("PARAMETER", StringComparison.Ordinal)))
                        method.Add(parameter);

                    schema._methods[(string?)element.Attribute("NAME") ?? string.Empty] = method;
                    break;
            }

        return schema;
    }

    private void Add(XElement element)
    {
        var name = (string?)element.Attribute("NAME");
        if (name is null)
            return;

        var local = element.Name.LocalName;
        var isReference = local.Contains("REF", StringComparison.Ordinal);
        var type = isReference ? "reference" : (string?)element.Attribute("TYPE") ?? "string";
        _properties[name] = new WsmanPropertyType(type, local.EndsWith("ARRAY", StringComparison.Ordinal));
    }

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
