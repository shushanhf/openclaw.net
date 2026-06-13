using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Core.Skills;

public sealed class MetaClarifyValidator
{
    public MetaClarifyValidationResult ValidateAndNormalize(string? input, MetaClarifySchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (MatchesCancelWord(input, schema.CancelWords))
            return MetaClarifyValidationResult.Invalid("user_input_cancelled");

        if (!string.Equals(schema.Mode, "form", StringComparison.OrdinalIgnoreCase))
            return MetaClarifyValidationResult.Valid(input ?? string.Empty);

        if (string.IsNullOrWhiteSpace(input))
            return MetaClarifyValidationResult.Invalid("clarify_input_required");

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(input);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return MetaClarifyValidationResult.Invalid("clarify_invalid_json");
        }

        if (root.ValueKind != JsonValueKind.Object)
            return MetaClarifyValidationResult.Invalid("clarify_invalid_shape");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var field in schema.Fields)
            {
                if (!TryResolveFieldValue(root, field, out var value, out var failureCode))
                    return MetaClarifyValidationResult.Invalid(failureCode!);

                WriteField(writer, field.Name, value);
            }

            writer.WriteEndObject();
        }

        return MetaClarifyValidationResult.Valid(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static bool MatchesCancelWord(string? input, IReadOnlyList<string> cancelWords)
    {
        if (string.IsNullOrWhiteSpace(input) || cancelWords.Count == 0)
            return false;

        var normalized = input.Trim();
        foreach (var cancelWord in cancelWords)
        {
            if (normalized.Equals(cancelWord, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryResolveFieldValue(JsonElement root, MetaClarifyField field, out JsonElement value, out string? failureCode)
    {
        failureCode = null;
        if (!root.TryGetProperty(field.Name, out value))
        {
            if (field.DefaultValue is JsonElement defaultValue)
            {
                value = defaultValue;
                return true;
            }

            if (field.Required)
            {
                failureCode = "clarify_required_field_missing";
                return false;
            }

            value = default;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (field.Required)
            {
                failureCode = "clarify_required_field_missing";
                return false;
            }

            return true;
        }

        if (string.Equals(field.Type, "string", StringComparison.OrdinalIgnoreCase))
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                failureCode = "clarify_invalid_type";
                return false;
            }

            var stringValue = value.GetString() ?? string.Empty;
            if (field.MinLength is int minLength && stringValue.Length < minLength)
            {
                failureCode = "clarify_min_length";
                return false;
            }

            if (field.MaxLength is int maxLength && stringValue.Length > maxLength)
            {
                failureCode = "clarify_max_length";
                return false;
            }

            return true;
        }

        if (string.Equals(field.Type, "enum", StringComparison.OrdinalIgnoreCase))
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                failureCode = "clarify_invalid_type";
                return false;
            }

            var option = value.GetString() ?? string.Empty;
            if (!field.Options.Contains(option, StringComparer.Ordinal))
            {
                failureCode = "clarify_invalid_option";
                return false;
            }

            return true;
        }

        if (string.Equals(field.Type, "number", StringComparison.OrdinalIgnoreCase))
        {
            if (!value.TryGetDouble(out var numericValue))
            {
                failureCode = "clarify_invalid_type";
                return false;
            }

            if (field.Min is double min && numericValue < min)
            {
                failureCode = "clarify_min";
                return false;
            }

            if (field.Max is double max && numericValue > max)
            {
                failureCode = "clarify_max";
                return false;
            }

            return true;
        }

        if (string.Equals(field.Type, "integer", StringComparison.OrdinalIgnoreCase))
        {
            if (!value.TryGetInt64(out var integerValue))
            {
                failureCode = "clarify_invalid_type";
                return false;
            }

            if (field.Min is double min && integerValue < min)
            {
                failureCode = "clarify_min";
                return false;
            }

            if (field.Max is double max && integerValue > max)
            {
                failureCode = "clarify_max";
                return false;
            }

            return true;
        }

        if (string.Equals(field.Type, "boolean", StringComparison.OrdinalIgnoreCase))
        {
            if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                failureCode = "clarify_invalid_type";
                return false;
            }

            return true;
        }

        failureCode = "clarify_invalid_type";
        return false;
    }

    private static void WriteField(Utf8JsonWriter writer, string propertyName, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            return;

        writer.WritePropertyName(propertyName);
        value.WriteTo(writer);
    }
}

public sealed class MetaClarifyValidationResult
{
    private MetaClarifyValidationResult(bool isValid, string? normalizedOutput, string? failureCode)
    {
        IsValid = isValid;
        NormalizedOutput = normalizedOutput;
        FailureCode = failureCode;
    }

    public bool IsValid { get; }

    public string? NormalizedOutput { get; }

    public string? FailureCode { get; }

    public static MetaClarifyValidationResult Valid(string normalizedOutput) => new(true, normalizedOutput, null);

    public static MetaClarifyValidationResult Invalid(string failureCode) => new(false, null, failureCode);
}