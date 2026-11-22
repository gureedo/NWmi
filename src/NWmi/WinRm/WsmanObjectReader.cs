using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NWmi.WinRm;

/// <summary>
/// Turns the XML instances of a WS-Management reply into <see cref="WmiInstance"/>s. The XML carries no CIM types: the
/// class definition (<see cref="WsmanClassSchema"/>) types the values when the server gives one. Without it a value is: null
/// for <c>xsi:nil</c>, a <see cref="bool"/> for <c>true</c>/<c>false</c>, a <see cref="DateTime"/> in UTC for a
/// <c>cim:Datetime</c> and a <see cref="TimeSpan"/> for a <c>cim:Interval</c>, a <see cref="WmiInstance"/> holding the keys for
/// a reference, a <see cref="WmiInstance"/> for a nested instance, an <c>object?[]</c> for a repeated element, and otherwise
/// the text. A one-element array cannot then be told from a scalar and reads as one.
/// </summary>
internal static partial class WsmanObjectReader
{
    public static WmiInstance Read(XElement instance, XElement? endpointReference, IReadOnlyList<string>? knownKeys,
        string? server, string? namespacePath, string? fallbackClassName = null,
        Func<string, WsmanClassSchema?>? schemas = null, bool isClassRow = false)
    {
        var keys = endpointReference is null
            ? knownKeys ?? []
            : [.. SelectorElements(endpointReference).Select(s => (string?)s.Attribute("Name")).OfType<string>()];
        var className = instance.Name.LocalName == "XmlFragment"
            ? fallbackClassName ?? "XmlFragment"
            : ClassNameOf(instance);
        var values = ReadProperties(instance, className, schemas, server, namespacePath);
        return WmiInstance.FromWinRm(className, values, schemas?.Invoke(className)?.Class, keys, server, namespacePath,
            isClassRow);
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
            ? ReferencePath(selector)?.ToString() ?? string.Empty
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

        // By local name: in the fragments of a query that names its properties the element is not in the cim namespace.
        if (IsDatetimeElement(first, "Datetime"))
            return ParseDatetime(first.Value.Trim());

        if (IsDatetimeElement(first, "Interval"))
            return ParseInterval(first.Value.Trim());

        if (first.Name.LocalName == "Address" &&
            element.Descendants().Any(e => e.Name.LocalName == "ReferenceParameters"))
            return ReferencePath(element) is { } path ? WmiInstance.FromReference(path) : null;

        var nestedClass = TypeClassOf(element) ?? element.Name.LocalName;
        var values = ReadProperties(element, nestedClass, schemas, server, ns);
        return WmiInstance.FromWinRm(nestedClass, values, schemas?.Invoke(nestedClass)?.Class, [], server, ns);
    }

    /// <summary>
    /// A <c>cim:Datetime</c> or <c>cim:Interval</c>, or the unqualified one of a fragment. A property of an embedded
    /// instance that happens to have that name is in the class's namespace, or under an element typed by <c>xsi:type</c>.
    /// </summary>
    private static bool IsDatetimeElement(XElement element, string localName) =>
        element.Name.LocalName == localName && !element.HasElements && element.Parent!.Elements().Count() == 1 &&
        (element.Name.Namespace == Wsman.Cim ||
         (element.Name.Namespace == XNamespace.None && TypeClassOf(element.Parent) is null));

    private const string WmiResourcePrefix = "http://schemas.microsoft.com/wbem/wsman/1/wmi/";

    /// <summary>
    /// A reference is an endpoint reference: its resource URI names the class, and its selectors the keys and, in
    /// <c>__cimnamespace</c>, the namespace. A resource URI under the WMI prefix (<c>.../wmi/root/cimv2/Win32_Account</c>)
    /// names the namespace too.
    /// </summary>
    private static WmiObjectPath? ReferencePath(XElement element)
    {
        var uri = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResourceURI")?.Value.Trim();
        var slash = uri?.LastIndexOf('/') ?? -1;
        var className = uri?[(slash + 1)..];
        if (string.IsNullOrEmpty(className))
            return null;

        string? @namespace = null;
        if (uri!.StartsWith(WmiResourcePrefix, StringComparison.OrdinalIgnoreCase) && slash > WmiResourcePrefix.Length)
            @namespace = uri[WmiResourcePrefix.Length..slash];

        var keys = new List<KeyValuePair<string, object>>();
        foreach (var selector in SelectorElements(element))
        {
            var name = (string?)selector.Attribute("Name") ?? string.Empty;
            if (string.Equals(name, "__cimnamespace", StringComparison.OrdinalIgnoreCase))
                @namespace = selector.Value.Trim();
            else
                keys.Add(new KeyValuePair<string, object>(name, SelectorValue(selector)));
        }

        return new WmiObjectPath(className, keys, string.IsNullOrEmpty(@namespace) ? null : @namespace.Replace('/', '\\'));
    }

    /// <summary>An xs:dateTime as a <see cref="DateTime"/> in UTC; the text itself when it does not parse.</summary>
    internal static object ParseDatetime(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : iso;

    /// <summary>An xs:duration as a <see cref="TimeSpan"/>; the text itself when it does not parse.</summary>
    internal static object ParseInterval(string duration)
    {
        if (!DurationPattern().IsMatch(duration))
            return duration;

        try
        {
            return XmlConvert.ToTimeSpan(duration);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // An interval longer than TimeSpan holds (DMTF goes to 99999999 days) stays text too.
            return duration;
        }
    }

    [GeneratedRegex(@"^-?P")]
    private static partial Regex DurationPattern();
}
