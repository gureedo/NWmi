using System.Globalization;
using NWmi.Client;

namespace NWmi.Cli;

/// <summary>One line per object: <c>Name=value</c> pairs separated by spaces.</summary>
internal sealed class TextResultWriter(TextWriter output) : IResultWriter
{
    public ValueTask BeginAsync() => ValueTask.CompletedTask;

    public async ValueTask WriteAsync(WmiInstance obj) =>
        await output
            .WriteLineAsync(string.Join(" ", obj.Properties.Select(p => $"{p.Name}={Format(p.Value)}")))
            .ConfigureAwait(false);

    public ValueTask EndAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static string Format(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        object?[] items => "[" + string.Join(", ", items.Select(Format)) + "]",
        WmiInstance embedded => "{" + string.Join(" ", embedded.Properties.Select(p => $"{p.Name}={Format(p.Value)}")) +
                                "}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
