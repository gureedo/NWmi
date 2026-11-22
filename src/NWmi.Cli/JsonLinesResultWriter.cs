using System.Text.Json;

namespace NWmi.Cli;

/// <summary>One compact JSON object per line, flushed as each arrives.</summary>
internal sealed class JsonLinesResultWriter(Stream stdout) : IResultWriter
{
    private readonly Utf8JsonWriter _json = new(stdout);

    public ValueTask BeginAsync() => ValueTask.CompletedTask;

    public ValueTask WriteAsync(WmiInstance obj)
    {
        JsonObjectWriter.Write(_json, obj);
        _json.Flush();
        _json.Reset();
        stdout.WriteByte(10);
        return ValueTask.CompletedTask;
    }

    public ValueTask EndAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _json.DisposeAsync();
}
