using System.Text.Json;
using NWmi.Client;

namespace NWmi.Cli;

/// <summary>All objects as one indented JSON array.</summary>
internal sealed class JsonResultWriter(Stream stdout) : IResultWriter
{
    private readonly Utf8JsonWriter _json = new(stdout, new JsonWriterOptions { Indented = true });

    public ValueTask BeginAsync()
    {
        _json.WriteStartArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(WmiInstance obj)
    {
        JsonObjectWriter.Write(_json, obj);
        return ValueTask.CompletedTask;
    }

    public ValueTask EndAsync()
    {
        _json.WriteEndArray();
        _json.Flush();
        stdout.WriteByte(10);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _json.DisposeAsync();
}
