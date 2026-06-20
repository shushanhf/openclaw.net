using System.Text.RegularExpressions;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Parses /loop commands from CLI and chat channels.
/// Uses [GeneratedRegex] for NativeAOT safety.
/// </summary>
public static partial class LoopCommandParser
{
    public const string CancelCommand = "cancel";
    public const string StatusCommand = "status";

    [GeneratedRegex(@"^/loop\s+(?<value>\d+)\s*(?<unit>s|m|h)\s+(?<prompt>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LoopCommandRegex();

    /// <summary>
    /// Tries to parse a /loop command. Returns null if text is not a /loop command.
    /// </summary>
    public static LoopCommand? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/loop", StringComparison.OrdinalIgnoreCase))
            return null;

        var trimmed = text.Trim();

        // /loop cancel / /loop stop
        if (trimmed.Equals("/loop cancel", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/loop stop", StringComparison.OrdinalIgnoreCase))
        {
            return new LoopCommand { Action = LoopAction.Cancel };
        }

        // /loop status
        if (trimmed.Equals("/loop status", StringComparison.OrdinalIgnoreCase))
        {
            return new LoopCommand { Action = LoopAction.Status };
        }

        // /loop <value><unit> <prompt>
        var match = LoopCommandRegex().Match(trimmed);
        if (!match.Success)
        {
            return new LoopCommand { Action = LoopAction.Invalid };
        }

        var interval = $"{match.Groups["value"].Value}{match.Groups["unit"].Value}";
        var prompt = match.Groups["prompt"].Value.Trim();

        return new LoopCommand
        {
            Action = LoopAction.Schedule,
            Interval = interval,
            Prompt = prompt
        };
    }
}

public enum LoopAction
{
    Schedule,
    Cancel,
    Status,
    Invalid
}

public sealed class LoopCommand
{
    public LoopAction Action { get; init; }
    public string? Interval { get; init; }
    public string? Prompt { get; init; }
}
