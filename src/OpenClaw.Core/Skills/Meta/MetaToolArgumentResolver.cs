using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Core.Skills;

public sealed class MetaToolArgumentResolver
{
    private readonly MetaTemplateRenderer _renderer;

    public MetaToolArgumentResolver(MetaTemplateRenderer renderer)
    {
        _renderer = renderer;
    }

    public string Resolve(string? compositionToolArgsJson, string? withJson, string? stepToolArgsJson, MetaExecutionContext context)
    {
        var merged = new JsonObject();
        MergeInto(merged, compositionToolArgsJson);
        MergeInto(merged, withJson);
        MergeInto(merged, stepToolArgsJson);

        string rendered;
        try
        {
            rendered = _renderer.Render(Serialize(merged), context);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("invalid_tool_args", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rendered);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("invalid_tool_args");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("invalid_tool_args");

            return document.RootElement.GetRawText();
        }
    }

    private static void MergeInto(JsonObject target, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        JsonNode? parsedNode;
        try
        {
            parsedNode = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("invalid_tool_args");
        }

        var node = parsedNode as JsonObject;
        if (node is null)
            throw new InvalidOperationException("invalid_tool_args");

        foreach (var property in node)
            target[property.Key] = property.Value?.DeepClone();
    }

    private static string Serialize(JsonObject value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}