using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Loops;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Tool that lets the model explicitly declare a loop task is complete.
/// When called with status="complete", signals the LoopTerminationDetector
/// to cancel the loop for this session.
/// </summary>
public sealed class LoopControlTool : IToolWithContext
{
    private readonly LoopTerminationDetector _detector;

    public LoopControlTool(LoopTerminationDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public string Name => "loop_control";

    public string Description =>
        "Control the active /loop recurring task. Use status='complete' when the loop task is fully done. " +
        "Do NOT use this for ongoing progress — only for final completion.";

    public string ParameterSchema => """
        {"type":"object","properties":{"status":{"type":"string","enum":["complete"],"description":"Set to 'complete' when the loop task is finished."}},"required":["status"]}
        """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        return ValueTask.FromResult("Error: loop_control requires session context.");
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "Error: arguments payload is empty.";

        string? status;
        try
        {
            using var args = JsonDocument.Parse(argumentsJson);
            if (!args.RootElement.TryGetProperty("status", out var statusElement))
                return "Error: status is required.";

            status = statusElement.GetString();
        }
        catch (JsonException)
        {
            return "Error: arguments must be valid JSON.";
        }

        if (string.IsNullOrWhiteSpace(status))
            return "Error: status is required.";

        if (!status.Equals("complete", StringComparison.OrdinalIgnoreCase))
            return $"Error: unsupported status '{status}'. Only 'complete' is allowed.";

        await _detector.OnToolCompleteAsync(context.Session.Id, ct);
        return "Loop marked as complete. The recurring task has been stopped.";
    }
}
