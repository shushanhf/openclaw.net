using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Agent.Goal;

/// <summary>
/// Builds system prompts for Goal activation (turn start) and check (on stop).
/// </summary>
public static class GoalPromptTemplates
{
    /// <summary>
    /// Builds the activation prompt injected once at the start of each turn
    /// when a goal is active.
    /// </summary>
    public static string BuildActivationPrompt(SessionGoal goal)
    {
        return $"""
            **Active Goal**
            A session-scoped goal is now active with the following objective:
            <objective>{goal.Objective}</objective>

            **Your Behavior**
            - Treat the objective itself as your directive. Do NOT pause to ask the user what to do.
            - The system will automatically continue you if you stop before the goal is achieved.
            - When the goal is fully achieved, use the update_goal tool with status='complete'.
            - If you're genuinely blocked after repeated attempts, use update_goal with status='blocked'.

            **Completion Audit**
            Before declaring the goal complete, derive concrete requirements from the objective.
            For each requirement, identify authoritative evidence. Uncertain evidence means NOT achieved.
            """;
    }

    /// <summary>
    /// Builds the check prompt injected when the model stops and a goal is active.
    /// This prompt tells the model to review progress and continue working.
    /// </summary>
    public static string BuildCheckPrompt(SessionGoal goal, int iteration, int maxIterations)
    {
        var budgetLine = goal.TokenBudget > 0
            ? $"**Budget**: Used {goal.TokensUsed} / Budget {goal.TokenBudget} / Remaining {goal.RemainingBudget}"
            : "**Budget**: No limit set.";

        return $"""
            **Goal Check — Continue Working**
            You were working toward this objective: <objective>{goal.Objective}</objective>

            1. REVIEW all work done so far
            2. DETERMINE whether the objective has been FULLY achieved
            3. If ACHIEVED → use update_goal tool with status='complete'
            4. If NOT ACHIEVED → CONTINUE working without asking the user

            {budgetLine}
            **Fidelity**: Optimize for movement toward the requested end state. Do NOT substitute easier solutions.
            **Blocked Audit**: Only mark blocked after 3+ consecutive turns with the same blocker.
            Iteration: {iteration}/{maxIterations}
            """;
    }

    /// <summary>
    /// Formats the TUI footer line showing goal status.
    /// </summary>
    public static string FormatGoalFooterLine(SessionGoal? goal)
    {
        if (goal is null) return string.Empty;

        return goal.Status switch
        {
            GoalStatus.Active when goal.TokenBudget > 0 =>
                $"Pursuing goal ({goal.TokensUsed}/{goal.TokenBudget})",
            GoalStatus.Active =>
                $"Pursuing goal: {Truncate(goal.Objective, 40)}",
            GoalStatus.Paused =>
                "Goal paused (/goal resume)",
            GoalStatus.Blocked =>
                "Goal blocked (/goal resume)",
            GoalStatus.BudgetLimited =>
                $"Goal unmet ({goal.TokensUsed}/{goal.TokenBudget})",
            GoalStatus.UsageLimited =>
                "Goal hit usage limits (/goal resume)",
            GoalStatus.Complete =>
                $"Goal achieved ({goal.TokensUsed})",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Formats a TUI progress bar showing token usage.
    /// Returns null if no budget is set.
    /// </summary>
    public static string? FormatProgressBar(SessionGoal? goal)
    {
        if (goal is null || goal.TokenBudget <= 0) return null;

        var pct = Math.Clamp((double)goal.TokensUsed / goal.TokenBudget, 0, 1);
        var barWidth = 20;
        var filled = (int)(pct * barWidth);
        var empty = barWidth - filled;

        var bar = new char[barWidth + 2];
        bar[0] = '[';
        for (int i = 0; i < filled; i++) bar[i + 1] = '=';
        if (filled < barWidth) bar[filled + 1] = '>';
        for (int i = filled + 2; i <= barWidth; i++) bar[filled + 1] = ' ';
        bar[barWidth + 1] = ']';

        return $"{new string(bar)} {pct * 100:F0}% ({goal.TokensUsed}/{goal.TokenBudget})";
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
