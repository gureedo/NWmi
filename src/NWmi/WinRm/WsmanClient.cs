using System.Globalization;
using System.Xml.Linq;
using NWmi.Wmi;

namespace NWmi.WinRm;

/// <summary>Namespaces and actions of WS-Management as the Windows WMI provider uses them.</summary>
internal static class Wsman
{
    public static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    public static readonly XNamespace Addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    public static readonly XNamespace Man = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    public static readonly XNamespace Enumeration = "http://schemas.xmlsoap.org/ws/2004/09/enumeration";
    public static readonly XNamespace Eventing = "http://schemas.xmlsoap.org/ws/2004/08/eventing";
    public static readonly XNamespace MsMan = "http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd";
    public static readonly XNamespace Cim = "http://schemas.dmtf.org/wbem/wscim/1/common";
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public const string DmtfSchema = "http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/";
    public const string WqlDialect = "http://schemas.microsoft.com/wbem/wsman/1/WQL";
    public const string WmiRoot = "http://schemas.microsoft.com/wbem/wsman/1/wmi/";
    public const string Anonymous = "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";

    public const string ActionGet = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Get";
    public const string ActionPut = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Put";
    public const string ActionCreate = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create";
    public const string ActionDelete = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete";
    public const string ActionEnumerate = "http://schemas.xmlsoap.org/ws/2004/09/enumeration/Enumerate";
    public const string ActionPull = "http://schemas.xmlsoap.org/ws/2004/09/enumeration/Pull";
    public const string ActionRelease = "http://schemas.xmlsoap.org/ws/2004/09/enumeration/Release";
    public const string ActionSubscribe = "http://schemas.xmlsoap.org/ws/2004/08/eventing/Subscribe";
    public const string ActionUnsubscribe = "http://schemas.xmlsoap.org/ws/2004/08/eventing/Unsubscribe";
}

/// <summary>A WS-Management fault that is a timeout: an event pull that simply saw nothing happen.</summary>
internal sealed class WsmanTimedOutException(string message) : WsmanException(message);

/// <summary>A WS-Management option (<c>w:Option</c>): a name, an XML Schema type such as <c>xs:string</c>, and the text.</summary>
internal readonly record struct WsmanOption(string Name, string Type, string Value);

/// <summary>
/// Builds WS-Management SOAP requests for one WMI namespace and reads the replies. Faults come back as a
/// <see cref="WmiException"/> carrying the WBEM status that the provider's <c>MSFT_WmiError</c> detail names.
/// </summary>
internal sealed class WsmanClient(WsmanTransport transport, string namespacePath, TimeSpan operationTimeout)
{
    public TimeSpan Timeout => operationTimeout;

    /// <summary>The namespace with <c>/</c> separators (<c>root/cimv2</c>).</summary>
    public string Namespace { get; } = namespacePath.Replace('\\', '/').Trim('/');

    /// <summary>The resource URI for a class (<c>.../root/cimv2/Win32_Process</c>), or the wildcard that WQL queries use.</summary>
    public string ResourceUri(string className) => $"{Wsman.WmiRoot}{Namespace}/{className}";

    /// <summary>Sends one request and returns the SOAP body.</summary>
    public async Task<XElement> CallAsync(
        string resourceUri, string action, IEnumerable<KeyValuePair<string, string>>? selectors, XElement? body,
        CancellationToken cancellationToken, TimeSpan? timeout = null,
        IEnumerable<WsmanOption>? options = null)
    {
        var header = new XElement(Wsman.Soap + "Header",
            new XElement(Wsman.Addressing + "To", transport.Endpoint.AbsoluteUri),
            new XElement(Wsman.Man + "ResourceURI", Mandatory(), resourceUri),
            new XElement(Wsman.Addressing + "ReplyTo",
                new XElement(Wsman.Addressing + "Address", Mandatory(), Wsman.Anonymous)),
            new XElement(Wsman.Addressing + "Action", Mandatory(), action),
            new XElement(Wsman.Man + "MaxEnvelopeSize", Mandatory(), "512000"),
            new XElement(Wsman.Addressing + "MessageID", $"uuid:{Guid.NewGuid()}"),
            new XElement(Wsman.Man + "Locale", new XAttribute(XNamespace.Xml + "lang", "en-US"),
                new XAttribute(Wsman.Soap + "mustUnderstand", "false")),
            new XElement(Wsman.MsMan + "DataLocale", new XAttribute(XNamespace.Xml + "lang", "en-US"),
                new XAttribute(Wsman.Soap + "mustUnderstand", "false")),
            new XElement(Wsman.Man + "OperationTimeout",
                XmlConvertDuration(timeout ?? operationTimeout)));

        var selectorList = selectors?.ToList();
        if (selectorList is { Count: > 0 })
            header.Add(new XElement(Wsman.Man + "SelectorSet",
                selectorList.Select(s =>
                    new XElement(Wsman.Man + "Selector", new XAttribute("Name", s.Key), s.Value))));

        var optionList = options?.ToList();
        if (optionList is { Count: > 0 })
            header.Add(new XElement(Wsman.Man + "OptionSet",
                new XAttribute(XNamespace.Xmlns + "xs", "http://www.w3.org/2001/XMLSchema"),
                optionList.Select(o => new XElement(Wsman.Man + "Option",
                    new XAttribute("Name", o.Name), new XAttribute("Type", o.Type), o.Value))));

        var envelope = new XElement(Wsman.Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Wsman.Soap),
            new XAttribute(XNamespace.Xmlns + "a", Wsman.Addressing),
            new XAttribute(XNamespace.Xmlns + "w", Wsman.Man),
            new XAttribute(XNamespace.Xmlns + "cim", Wsman.Cim),
            header,
            new XElement(Wsman.Soap + "Body", body));

        var text = await transport
            .SendAsync(envelope.ToString(SaveOptions.DisableFormatting), IsIdempotent(action), cancellationToken)
            .ConfigureAwait(false);
        XElement response;
        try
        {
            response = XElement.Parse(text);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new WsmanException("The WinRM listener returned something that is not a SOAP message.", ex);
        }

        var responseBody = response.Element(Wsman.Soap + "Body")
                           ?? throw new WsmanException("The WinRM reply has no SOAP body.");
        if (responseBody.Element(Wsman.Soap + "Fault") is { } fault)
            throw ReadFault(fault);

        return responseBody;

        static XAttribute Mandatory() => new(Wsman.Soap + "mustUnderstand", "true");
    }

    /// <summary>
    /// Whether running a request twice is harmless. A Get or the start of an enumeration only reads; everything else
    /// changes something on the server or moves an enumeration along, and a method call (whose action is a per-method
    /// URI) can do anything.
    /// </summary>
    internal static bool IsIdempotent(string action) => action is Wsman.ActionGet or Wsman.ActionEnumerate;

    private static string XmlConvertDuration(TimeSpan timeout) =>
        $"PT{Math.Max(1, timeout.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture)}S";

    private static Exception ReadFault(XElement fault)
    {
        var reason = fault.Element(Wsman.Soap + "Reason")?.Elements().FirstOrDefault()?.Value.Trim() ??
                     "WS-Management fault";
        var subcode = fault.Element(Wsman.Soap + "Code")?.Element(Wsman.Soap + "Subcode")?.Element(Wsman.Soap + "Value")
            ?.Value;
        if (subcode is not null && subcode.EndsWith(":TimedOut", StringComparison.Ordinal))
            return new WsmanTimedOutException(reason);

        var wmiError = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "MSFT_WmiError");
        var cimStatus = wmiError?.Elements().FirstOrDefault(e => e.Name.LocalName == "CIMStatusCode")?.Value;
        var message = wmiError?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value.Trim();
        if (int.TryParse(cimStatus, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            return new WmiException(unchecked((int)MapCimStatus(code)), message ?? reason);

        if (subcode is not null && subcode.EndsWith(":AccessDenied", StringComparison.Ordinal))
            return new WmiException(unchecked((int)WbemStatus.AccessDenied), reason);

        var detail = fault.Descendants(Wsman.Man + "FaultDetail").FirstOrDefault()?.Value;
        if (subcode is not null && subcode.EndsWith(":DestinationUnreachable", StringComparison.Ordinal) ||
            detail is not null && detail.EndsWith("/InvalidResourceURI", StringComparison.Ordinal))
            // No such instance (or class) at that resource URI and selectors.
            return new WmiException(unchecked((int)WbemStatus.NotFound), reason);

        return new WsmanException($"{reason}{(subcode is null ? "" : $" ({subcode})")}");
    }

    /// <summary>CIM status codes (DMTF DSP0200 2.2.9) as the nearest WBEM status.</summary>
    private static WbemStatus MapCimStatus(int code) => (CimStatusCode)code switch
    {
        CimStatusCode.AccessDenied => WbemStatus.AccessDenied,
        CimStatusCode.InvalidNamespace => WbemStatus.InvalidNamespace,
        CimStatusCode.InvalidParameter => WbemStatus.InvalidParameter,
        CimStatusCode.InvalidClass => WbemStatus.InvalidClass,
        CimStatusCode.NotFound => WbemStatus.NotFound,
        CimStatusCode.NotSupported => WbemStatus.NotSupported,
        CimStatusCode.ClassHasChildren => WbemStatus.ClassHasChildren,
        CimStatusCode.ClassHasInstances => WbemStatus.ClassHasInstances,
        CimStatusCode.InvalidSuperclass => WbemStatus.InvalidSuperclass,
        CimStatusCode.AlreadyExists => WbemStatus.AlreadyExists,
        CimStatusCode.NoSuchProperty => WbemStatus.InvalidProperty,
        CimStatusCode.TypeMismatch => WbemStatus.TypeMismatch,
        CimStatusCode.QueryLanguageNotSupported => WbemStatus.InvalidQueryType,
        CimStatusCode.InvalidQuery => WbemStatus.InvalidQuery,
        CimStatusCode.MethodNotAvailable => WbemStatus.MethodNotImplemented,
        CimStatusCode.MethodNotFound => WbemStatus.InvalidMethod,
        CimStatusCode.ServerIsShuttingDown => WbemStatus.ShuttingDown,
        _ => WbemStatus.Failed,
    };

    /// <summary>Formats a value the way WS-Man text carries it.</summary>
    public static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
