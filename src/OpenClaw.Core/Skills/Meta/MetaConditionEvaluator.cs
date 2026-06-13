namespace OpenClaw.Core.Skills;

public sealed class MetaConditionEvaluator
{
    private readonly MetaTemplateRenderer _renderer;

    public MetaConditionEvaluator(MetaTemplateRenderer renderer)
    {
        _renderer = renderer;
    }

    public bool Evaluate(string? expression, MetaExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var candidate = expression.Trim();
        var template = candidate.Contains("{{", StringComparison.Ordinal) || candidate.Contains("{%", StringComparison.Ordinal)
            ? candidate
            : "{{ " + candidate + " }}";
        var rendered = _renderer.Render(template, context);

        return IsTruthy(rendered);
    }

    internal static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        if (bool.TryParse(normalized, out var boolValue))
            return boolValue;

        return !normalized.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("off", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("null", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("undefined", StringComparison.OrdinalIgnoreCase);
    }
}