using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Agent.Goal;

namespace OpenClaw.Tui;

/// <summary>
/// Extension methods for displaying Goal status in the TUI footer.
/// </summary>
public static class GoalTuiExtensions
{
    /// <summary>
    /// Formats the goal status line for the TUI footer.
    /// Returns an empty string if no goal exists or the goal is terminal.
    /// </summary>
    public static string FormatGoalFooterLine(IGoalService? goalService, string sessionId)
    {
        if (goalService is null) return string.Empty;

        var goal = goalService.GetGoal(sessionId);
        return GoalPromptTemplates.FormatGoalFooterLine(goal);
    }

    /// <summary>
    /// Formats the goal progress bar for the TUI footer.
    /// Returns null if no goal, no budget, or goal is not in a pursuable state.
    /// </summary>
    public static string? FormatGoalProgressBar(IGoalService? goalService, string sessionId)
    {
        if (goalService is null) return null;

        var goal = goalService.GetGoal(sessionId);
        if (goal is null || !goal.Status.IsPursuable()) return null;

        return GoalPromptTemplates.FormatProgressBar(goal);
    }

    /// <summary>
    /// Formats a compact goal status indicator (single line) for the TUI footer.
    /// Combines status text and progress bar when applicable.
    /// </summary>
    public static string FormatGoalStatusLine(IGoalService? goalService, string sessionId)
    {
        if (goalService is null) return string.Empty;

        var goal = goalService.GetGoal(sessionId);
        if (goal is null) return string.Empty;

        var statusText = GoalPromptTemplates.FormatGoalFooterLine(goal);
        var progressBar = GoalPromptTemplates.FormatProgressBar(goal);

        if (progressBar is not null)
            return $"{statusText} {progressBar}";

        return statusText;
    }
}
