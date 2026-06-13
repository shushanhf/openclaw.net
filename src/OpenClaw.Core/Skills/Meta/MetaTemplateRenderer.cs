using System.Net;
using System.Text;
using System.Text.Json;
using Jinja2.NET;

namespace OpenClaw.Core.Skills;

public sealed class MetaTemplateRenderer
{
    public string Render(string template, MetaExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);

        var environment = new Jinja2.NET.Environment(AppContext.BaseDirectory);
        var compiled = environment.FromString(template);
        RegisterFilters(compiled);

        return compiled.Render(new Dictionary<string, object>
        {
            ["input"] = context.Input,
            ["inputs"] = context.Inputs,
            ["outputs"] = context.Outputs.ToDictionary(static pair => pair.Key, static pair => (object)pair.Value, StringComparer.OrdinalIgnoreCase),
            ["steps"] = context.Steps
        });
    }

    private static void RegisterFilters(Template template)
    {
        template.RegisterFilter("xml_escape", static (value, _) => WebUtility.HtmlEncode(value?.ToString() ?? string.Empty));
        template.RegisterFilter("slugify", static (value, _) => Slugify(value?.ToString() ?? string.Empty));
        template.RegisterFilter("truncate", static (value, args) => Truncate(value?.ToString() ?? string.Empty, args));
        template.RegisterFilter("tojson", static (value, _) => ToJson(value));
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        var previousDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = ch;
                previousDash = false;
                continue;
            }

            if (previousDash)
                continue;

            buffer[index++] = '-';
            previousDash = true;
        }

        var slug = new string(buffer[..index]).Trim('-');
        return slug;
    }

    private static string Truncate(string value, object[]? args)
    {
        var maxLength = 80;
        if (args is { Length: > 0 })
            maxLength = Convert.ToInt32(args[0], System.Globalization.CultureInfo.InvariantCulture);

        if (maxLength <= 0)
            maxLength = 80;

        if (value.Length <= maxLength)
            return value;

        if (maxLength <= 3)
            return new string('.', maxLength);

        return value[..(maxLength - 3)].TrimEnd() + "...";
    }

    private static string ToJson(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteJsonValue(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement jsonElement:
                jsonElement.WriteTo(writer);
                return;
            case string stringValue:
                writer.WriteStringValue(stringValue);
                return;
            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                return;
            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                return;
            case sbyte sbyteValue:
                writer.WriteNumberValue(sbyteValue);
                return;
            case short shortValue:
                writer.WriteNumberValue(shortValue);
                return;
            case ushort ushortValue:
                writer.WriteNumberValue(ushortValue);
                return;
            case int intValue:
                writer.WriteNumberValue(intValue);
                return;
            case uint uintValue:
                writer.WriteNumberValue(uintValue);
                return;
            case long longValue:
                writer.WriteNumberValue(longValue);
                return;
            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                return;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                return;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                return;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                return;
            case IReadOnlyDictionary<string, string> stringDictionary:
                writer.WriteStartObject();
                foreach (var pair in stringDictionary)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteJsonValue(writer, pair.Value);
                }
                writer.WriteEndObject();
                return;
            case IReadOnlyDictionary<string, object> objectDictionary:
                writer.WriteStartObject();
                foreach (var pair in objectDictionary)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteJsonValue(writer, pair.Value);
                }
                writer.WriteEndObject();
                return;
            case System.Collections.IDictionary dictionary:
                writer.WriteStartObject();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName(entry.Key?.ToString() ?? string.Empty);
                    WriteJsonValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                return;
            case System.Collections.IEnumerable enumerable when value is not string:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                    WriteJsonValue(writer, item);
                writer.WriteEndArray();
                return;
            default:
                throw new InvalidOperationException($"Unsupported tojson value type '{value.GetType().FullName}'.");
        }
    }
}