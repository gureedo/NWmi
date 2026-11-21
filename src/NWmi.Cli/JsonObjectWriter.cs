using System.Globalization;
using System.Text.Json;
using NWmi.Client;

namespace NWmi.Cli;

/// <summary>
/// One object as JSON: <c>__CLASS</c>, then every property by name. Integers keep full precision, embedded objects
/// nest, arrays become JSON arrays, and a CIM datetime stays the string the server sent.
/// </summary>
internal static class JsonObjectWriter
{
    public static void Write(Utf8JsonWriter json, WmiObject obj)
    {
        json.WriteStartObject();
        json.WriteString("__CLASS", obj.ClassName);
        foreach (var name in obj.Properties.Keys)
        {
            json.WritePropertyName(name);
            WriteValue(json, obj[name]);
        }

        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null: json.WriteNullValue(); break;
            case bool b: json.WriteBooleanValue(b); break;
            case string s: json.WriteStringValue(s); break;
            case char c: json.WriteStringValue(c.ToString()); break;
            case sbyte or byte or short or ushort or int or uint or long:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong u: json.WriteNumberValue(u); break;
            case float f: json.WriteNumberValue(f); break;
            case double d: json.WriteNumberValue(d); break;
            case WmiObject embedded: Write(json, embedded); break;
            case object?[] items:
                json.WriteStartArray();
                foreach (var item in items)
                    WriteValue(json, item);

                json.WriteEndArray();
                break;
            default: json.WriteStringValue(TextResultWriter.Format(value)); break;
        }
    }
}
