using System.Collections;
using System.Globalization;
using NWmi.Wmi;

namespace NWmi.Client;

/// <summary>
/// The .NET form a property value has once read: a datetime as a <see cref="DateTime"/> in UTC (a timestamp) or a
/// <see cref="TimeSpan"/> (an interval), a reference as a <see cref="WmiInstance"/> holding the keys of the instance it
/// refers to, an array as an array of its element type (<c>string[]</c>, <c>uint[]</c>, ...).
/// </summary>
internal static class WmiValues
{
    /// <summary>
    /// <paramref name="value"/>, as decoded from the wire (a DMTF string, a path string, an <c>object?[]</c>), in its public
    /// form for <paramref name="type"/>. A value already in that form is returned unchanged; one that has no such form (a
    /// datetime with wildcards, a path that does not parse) stays as it is.
    /// </summary>
    public static object? ToPublic(object? value, WmiType type)
    {
        if (value is null || type == WmiType.Unknown)
            return value;

        if (!WmiTypeMapping.IsArray(type))
            return ToPublicScalar(value, type);

        if (value is string || value is not IEnumerable sequence)
            return value;

        var element = WmiTypeMapping.ElementType(type);
        return TypedArray([.. sequence.Cast<object?>().Select(e => ToPublicScalar(e, element))], element);
    }

    private static object? ToPublicScalar(object? value, WmiType type) => type switch
    {
        WmiType.DateTime => value switch
        {
            string text => DmtfDateTime.Parse(text) switch
            {
                DateTimeOffset moment => moment.UtcDateTime,
                var parsed => parsed,
            },
            DateTimeOffset moment => moment.UtcDateTime,
            DateTime moment => moment.ToUniversalTime(),
            _ => value,
        },
        WmiType.Reference => value is string path && CimObjectPath.TryParse(path, out var parsed) && parsed.IsInstance
            ? WmiInstance.FromReference(parsed)
            : value,
        _ => value,
    };

    /// <summary>
    /// The elements as an array of their CLR type: <c>T[]</c> when every element is a <c>T</c> (none null for a value
    /// type); for a datetime, <see cref="DateTime"/>[] or <see cref="TimeSpan"/>[] when they are all one kind. Otherwise,
    /// such as a mix of timestamps and intervals, an <c>object?[]</c>.
    /// </summary>
    private static Array TypedArray(object?[] elements, WmiType elementType)
    {
        var clr = ClrType(elementType);
        if (elementType == WmiType.DateTime)
            clr = elements.Length == 0 || elements.All(e => e is DateTime) ? typeof(DateTime)
                : elements.All(e => e is TimeSpan) ? typeof(TimeSpan)
                : null;

        if (clr is null || !elements.All(e => e is null ? !clr.IsValueType : e.GetType() == clr))
            return elements;

        var array = Array.CreateInstance(clr, elements.Length);
        for (var i = 0; i < elements.Length; i++)
            array.SetValue(elements[i], i);

        return array;
    }

    internal static Type? ClrType(WmiType type) => type switch
    {
        WmiType.Boolean => typeof(bool),
        WmiType.UInt8 => typeof(byte),
        WmiType.SInt8 => typeof(sbyte),
        WmiType.UInt16 => typeof(ushort),
        WmiType.SInt16 => typeof(short),
        WmiType.UInt32 => typeof(uint),
        WmiType.SInt32 => typeof(int),
        WmiType.UInt64 => typeof(ulong),
        WmiType.SInt64 => typeof(long),
        WmiType.Real32 => typeof(float),
        WmiType.Real64 => typeof(double),
        WmiType.Char16 => typeof(char),
        WmiType.String => typeof(string),
        WmiType.Reference or WmiType.Instance => typeof(WmiInstance),
        _ => null,
    };

    /// <summary>A datetime as the DMTF string the wire carries: a timestamp keeps its instant (sent as UTC for a <see cref="DateTime"/>).</summary>
    public static string? ToDmtf(object value) => value switch
    {
        DateTimeOffset moment => DmtfDateTime.Format(moment),
        DateTime moment => DmtfDateTime.Format(new DateTimeOffset(moment.ToUniversalTime(), TimeSpan.Zero)),
        TimeSpan interval => DmtfDateTime.Format(interval),
        _ => null,
    };

    /// <summary>
    /// The path a reference to <paramref name="instance"/> is sent as: for an instance that a reference was read into, the
    /// path it was read from; for any other, its namespace and relative path (<c>root\cimv2:Win32_Account.Name="x"</c>), the
    /// server left out since a reference names an instance on the server it is stored on.
    /// </summary>
    public static string ReferencePath(WmiInstance instance)
    {
        var system = instance.SystemProperties;
        if (instance.IsReference)
            return system.Path ?? system.RelativePath ?? system.ClassName;

        var relative = system.RelativePath ?? system.ClassName;
        return system.Namespace is null ? relative : $"{system.Namespace}:{relative}";
    }

    /// <summary>
    /// A key value as a path or a WinRM selector gives it (a string, <see cref="bool"/>, <see cref="long"/>,
    /// <see cref="ulong"/> or <see cref="double"/>) as the scalar <paramref name="type"/> the class declares; false when
    /// it does not convert.
    /// </summary>
    public static bool TryToKeyType(object value, WmiType type, out object converted)
    {
        converted = value;
        if (type is WmiType.DateTime or WmiType.Reference)
        {
            if (Convert.ToString(value, CultureInfo.InvariantCulture) is not { } text)
                return false;

            converted = ToPublic(text, type) ?? value;
            return converted is DateTime or TimeSpan or WmiInstance;
        }

        var clr = ClrType(type);
        if (clr is null || clr == typeof(WmiInstance))
            return false;

        if (clr.IsInstanceOfType(value))
            return true;

        try
        {
            converted = clr == typeof(string) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? value
                : clr == typeof(bool) && value is string flag ? bool.Parse(flag)
                : clr == typeof(char) && value is string { Length: 1 } letter ? letter[0]
                : Convert.ChangeType(value, clr, CultureInfo.InvariantCulture);
            return clr.IsInstanceOfType(converted);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>A key value as a path holds it: a reference as its path, a datetime as its DMTF string.</summary>
    public static object ToKeyValue(object value) => value switch
    {
        WmiInstance reference => ReferencePath(reference),
        DateTimeOffset or DateTime or TimeSpan => ToDmtf(value)!,
        _ => value,
    };
}
