using System.Globalization;
using System.Text.Json;

namespace NWmi.Cli;

/// <summary>
/// One object as JSON: <c>__CLASS</c>, then every property by name. Integers keep full precision, embedded objects
/// nest, arrays become JSON arrays, a reference is the path of the instance it refers to, a timestamp is an ISO 8601
/// string and an interval a <c>d.hh:mm:ss.fffffff</c> string.
/// </summary>
internal static class JsonObjectWriter
{
    public static void Write(Utf8JsonWriter json, WmiInstance obj)
    {
        json.WriteStartObject();
        json.WriteString("__CLASS", obj.SystemProperties.ClassName);
        foreach (var property in obj.Properties)
        {
            json.WritePropertyName(property.Name);
            WriteValue(json, property.Value, property.Type is WmiType.Reference or WmiType.ReferenceArray);
        }

        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value, bool isReference)
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
            case DateTime moment: json.WriteStringValue(moment.ToString("O", CultureInfo.InvariantCulture)); break;
            case WmiInstance reference when isReference: json.WriteStringValue(TextResultWriter.PathOf(reference)); break;
            case WmiInstance embedded: Write(json, embedded); break;
            case Array items:
                json.WriteStartArray();
                foreach (var item in items)
                    WriteValue(json, item, isReference);

                json.WriteEndArray();
                break;
            default: json.WriteStringValue(TextResultWriter.Format(value)); break;
        }
    }
}
