using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NWmi.Client;
using NWmi.Wmi;

namespace NWmi.WinRm;

/// <summary>
/// Turns the XML instances of a WS-Management reply into <see cref="WmiObject"/>s. The XML carries no CIM types,
/// so a value is: null for <c>xsi:nil</c>, a <see cref="bool"/> for <c>true</c>/<c>false</c>, a DMTF string for a
/// <c>cim:Datetime</c> or <c>cim:Interval</c> (as the binary protocol gives), a path string for a reference, a
/// <see cref="WmiObject"/> for a nested instance, an <c>object?[]</c> for a repeated element, and otherwise the text.
/// A one-element array cannot be told from a scalar and reads as one.
/// </summary>
internal static partial class WsmanObjectReader
{
    public static WmiObject Read(XElement instance, XElement? endpointReference, IReadOnlyList<string>? knownKeys,
        string? server, string? namespacePath, string? fallbackClassName = null,
        Func<string, WsmanClassSchema?>? schemas = null)
    {
        var keys = endpointReference is null
            ? knownKeys ?? []
            : [.. SelectorElements(endpointReference).Select(s => (string?)s.Attribute("Name")).OfType<string>()];
        var className = instance.Name.LocalName == "XmlFragment"
            ? fallbackClassName ?? "XmlFragment"
            : ClassNameOf(instance);
        return new WmiObject(className, ReadProperties(instance, className, schemas, server, namespacePath), keys,
            server, namespacePath);
    }

    /// <summary>
    /// The class a WQL query selects from. A query that names its properties comes back as bare <c>XmlFragment</c>s
    /// with neither a class nor an endpoint reference, so the class is taken from the query itself.
    /// </summary>
    public static string? ClassOfQuery(string query)
    {
        var match = FromClause().Match(query);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"\bFROM\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex FromClause();


    private static string ClassNameOf(XElement instance) =>
        instance.Name.LocalName; // xsi:type is "p:Win32_Process_Type", the element name is the class.

    /// <summary>
    /// The selectors of the endpoint reference itself. A selector of an association's reference key holds an endpoint
    /// reference of its own, whose selectors are the other instance's keys, not this one's.
    /// </summary>
    private static IEnumerable<XElement> SelectorElements(XElement epr) =>
        epr.Descendants().FirstOrDefault(e => e.Name.LocalName == "SelectorSet")?.Elements()
            .Where(e => e.Name.LocalName == "Selector") ?? [];

    /// <summary>A selector's value: its text, or the path of the instance when it is a reference.</summary>
    private static string SelectorValue(XElement selector) =>
        selector.Descendants().Any(e => e.Name.LocalName == "ReferenceParameters")
            ? ReferencePath(selector) ?? string.Empty
            : selector.Value.Trim();

    /// <summary>The class an instance element is typed as by its <c>xsi:type</c> (<c>p:Win32_ProcessStartup_Type</c>), if it has one.</summary>
    internal static string? TypeClassOf(XElement element)
    {
        var type = (string?)element.Attribute(Wsman.Xsi + "type");
        if (type is null)
            return null;

        type = type[(type.IndexOf(':') + 1)..];
        return type.EndsWith("_Type", StringComparison.Ordinal) ? type[..^5] : null;
    }

    private static List<KeyValuePair<string, object?>> ReadProperties(
        XElement instance, string className, Func<string, WsmanClassSchema?>? schemas, string? server, string? ns)
    {
        var schema = schemas?.Invoke(className);
        var result = new List<KeyValuePair<string, object?>>();
        foreach (var group in instance.Elements().GroupBy(e => e.Name.LocalName))
        {
            var items = group.ToList();
            WsmanPropertyType? type = schema is not null && schema.TryGetProperty(group.Key, out var known)
                ? known
                : null;
            object? value;
            if (type is { IsArray: true })
                // A typed array stays an array whatever its length; a lone nil is a null array.
                value = items.Count == 1 && IsNil(items[0])
                    ? null
                    : items.Select(i => ReadValue(i, type, schemas, server, ns)).ToArray();
            else
                value = items.Count == 1
                    ? ReadValue(items[0], type, schemas, server, ns)
                    : items.Select(i => ReadValue(i, type, schemas, server, ns)).ToArray();

            result.Add(new KeyValuePair<string, object?>(group.Key, value));
        }

        return result;
    }

    private static bool IsNil(XElement element) => (string?)element.Attribute(Wsman.Xsi + "nil") is "true" or "1";

    private static object? ReadValue(XElement element, WsmanPropertyType? type,
        Func<string, WsmanClassSchema?>? schemas,
        string? server, string? ns)
    {
        if (IsNil(element))
            return null;

        var first = element.Elements().FirstOrDefault();
        if (first is null)
        {
            var text = element.Value;
            if (type is { } typed)
                return WsmanClassSchema.Convert(text, typed.CimType);

            return text switch
            {
                "true" => true,
                "false" => false,
                _ => text,
            };
        }

        if (first.Name == Wsman.Cim + "Datetime")
            return ToDmtfDateTime(first.Value.Trim());

        if (first.Name == Wsman.Cim + "Interval")
            return ToDmtfInterval(first.Value.Trim());

        if (first.Name.LocalName == "Address" &&
            element.Descendants().Any(e => e.Name.LocalName == "ReferenceParameters"))
            return ReferencePath(element);

        var nestedClass = TypeClassOf(element) ?? element.Name.LocalName;
        return new WmiObject(nestedClass, ReadProperties(element, nestedClass, schemas, server, ns), [], server, ns);
    }

    /// <summary>A reference is an endpoint reference; its resource URI names the class and its selectors the keys.</summary>
    private static string? ReferencePath(XElement element)
    {
        var uri = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResourceURI")?.Value.Trim();
        var className = uri?[(uri.LastIndexOf('/') + 1)..];
        if (string.IsNullOrEmpty(className))
            return null;

        var keys = SelectorElements(element)
            .Select(s => new KeyValuePair<string, object>((string?)s.Attribute("Name") ?? string.Empty, SelectorValue(s)))
            .ToList();
        return new CimObjectPath(className, keys).RelativePath;
    }

    /// <summary>An ISO 8601 datetime as the DMTF <c>yyyymmddHHMMSS.mmmmmm+UUU</c> string.</summary>
    internal static string ToDmtfDateTime(string iso)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return iso;

        return DmtfDateTime.Format(value);
    }

    /// <summary>An xs:duration as the DMTF interval <c>ddddddddHHMMSS.mmmmmm:000</c>.</summary>
    internal static string ToDmtfInterval(string duration)
    {
        var match = DurationPattern().Match(duration);
        if (!match.Success)
            return duration;

        TimeSpan span;
        try
        {
            span = XmlConvert.ToTimeSpan(duration);
        }
        catch (FormatException)
        {
            return duration;
        }

        // A negative interval has no DMTF form.
        return span < TimeSpan.Zero ? duration : DmtfDateTime.Format(span);
    }

    [GeneratedRegex(@"^-?P")]
    private static partial Regex DurationPattern();
}
