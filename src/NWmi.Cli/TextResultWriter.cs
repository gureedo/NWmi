using System.Globalization;
using NWmi.Client;

namespace NWmi.Cli;

/// <summary>One line per object: <c>Name=value</c> pairs separated by spaces.</summary>
internal sealed class TextResultWriter(TextWriter output) : IResultWriter
{
    public ValueTask BeginAsync() => ValueTask.CompletedTask;

    public async ValueTask WriteAsync(WmiInstance obj) =>
        await output.WriteLineAsync(FormatProperties(obj)).ConfigureAwait(false);

    public ValueTask EndAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string FormatProperties(WmiInstance obj) =>
        string.Join(" ", obj.Properties.Select(p => $"{p.Name}={Format(p)}"));

    /// <summary>A property's value; a reference as the path of the instance it refers to.</summary>
    internal static string Format(WmiProperty property) =>
        Format(property.Value, property.Type is WmiType.Reference or WmiType.ReferenceArray);

    /// <summary>A value as text: a timestamp in ISO 8601 (<c>2026-11-21T08:30:00.0000000Z</c>), an interval as <c>d.hh:mm:ss.fffffff</c>.</summary>
    internal static string Format(object? value, bool isReference = false) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan interval => interval.ToString("c", CultureInfo.InvariantCulture),
        WmiInstance reference when isReference => PathOf(reference),
        WmiInstance embedded => "{" + FormatProperties(embedded) + "}",
        string text => text,
        Array items => "[" + string.Join(", ", items.Cast<object?>().Select(e => Format(e, isReference))) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static string PathOf(WmiInstance reference) =>
        reference.SystemProperties.Path ?? reference.SystemProperties.RelativePath ?? reference.SystemProperties.ClassName;
}
