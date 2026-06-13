namespace OpenClaw.Core.Skills;

public sealed class MetaRoutePlanner
{
    private readonly MetaConditionEvaluator _conditionEvaluator;

    public MetaRoutePlanner(MetaConditionEvaluator conditionEvaluator)
    {
        _conditionEvaluator = conditionEvaluator;
    }

    public string? SelectNextStep(MetaSkillStepDefinition step, MetaExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var route in step.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.When) || _conditionEvaluator.Evaluate(route.When, context))
                return route.To;
        }

        return null;
    }

    public void ApplyInitialRoutingBlocks(
        IReadOnlyList<MetaSkillStepDefinition> steps,
        HashSet<string> blocked,
        HashSet<string> pending)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(blocked);
        ArgumentNullException.ThrowIfNull(pending);

        foreach (var step in steps)
        {
            foreach (var route in step.Routes)
            {
                blocked.Add(route.To);
                pending.Remove(route.To);
            }
        }
    }

    public void ApplyCompletionRouting(
        MetaSkillStepDefinition step,
        MetaExecutionContext context,
        IReadOnlyDictionary<string, MetaSkillStepDefinition> stepById,
        HashSet<string> blocked,
        HashSet<string> pending,
        IReadOnlyDictionary<string, List<string>> dependentsByStep)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stepById);
        ArgumentNullException.ThrowIfNull(blocked);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(dependentsByStep);

        if (step.Routes.Count == 0)
            return;

        var selectedTarget = SelectNextStep(step, context);
        foreach (var route in step.Routes)
        {
            if (!stepById.ContainsKey(route.To))
                continue;

            if (string.Equals(route.To, selectedTarget, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Remove(route.To);
                pending.Add(route.To);
                continue;
            }

            BlockStepAndDependents(route.To, blocked, pending, dependentsByStep);
        }
    }

    private static void BlockStepAndDependents(
        string stepId,
        HashSet<string> blocked,
        HashSet<string> pending,
        IReadOnlyDictionary<string, List<string>> dependentsByStep)
    {
        var stack = new Stack<string>();
        stack.Push(stepId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!blocked.Add(current))
                continue;

            pending.Remove(current);

            if (!dependentsByStep.TryGetValue(current, out var dependents))
                continue;

            foreach (var dependent in dependents)
                stack.Push(dependent);
        }
    }
}