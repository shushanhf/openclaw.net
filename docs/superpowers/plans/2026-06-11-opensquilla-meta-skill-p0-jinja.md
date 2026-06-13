# OpenSquilla Meta-Skill P0 + Jinja Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the missing OpenSquilla-native meta-skill DSL and clarify/Jinja runtime semantics in the shared Core layer, then wire both OpenClaw runtimes to the shared implementation with regression coverage and migration-doc updates.

**Architecture:** Keep parser, condition evaluation, Jinja rendering, tool-argument merging, route activation, and clarify validation in `OpenClaw.Core` so `OpenClaw.Agent` and `OpenClaw.MicrosoftAgentFrameworkAdapter` do not drift. The runtime projects should remain orchestration shells that call shared Core helpers and preserve existing `with`, `with.route`, `output_contract`, retry, timeout, and structured-envelope behavior.

**Tech Stack:** .NET 10, `System.Text.Json`, xUnit, NSubstitute, `Jinja2.NET` 1.4.1

---

## Worktree And Constraints

- Create and use a dedicated worktree before Task 1.
- Preserve NativeAOT friendliness in Core code; keep Jinja integration scoped and avoid reflection-heavy convenience patterns outside the package boundary.
- Do not change existing public meta-skill behavior unless the spec explicitly requires it.
- Keep the shared runtime logic in Core, not duplicated in `AgentRuntime` and `MafAgentRuntime`.

## File Structure

### Existing files to modify

- Modify: `src/OpenClaw.Core/OpenClaw.Core.csproj`
  Responsibility: add the selected `Jinja2.NET` dependency.
- Modify: `src/OpenClaw.Core/Skills/SkillModels.cs`
  Responsibility: add typed DSL models for composition-level tool args, route arrays, output choices, tool allowlists, and clarify schema.
- Modify: `src/OpenClaw.Core/Skills/SkillLoader.cs`
  Responsibility: parse and validate the new DSL fields while preserving existing meta-skill contracts.
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
  Responsibility: replace local ad-hoc meta helpers with Core-layer services for Jinja, `when`, route arrays, tool args, output choice checks, and clarify handling.
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
  Responsibility: mirror the same Core-layer behavior in the MAF runtime.
- Modify: `src/OpenClaw.Tests/SkillTests.cs`
  Responsibility: parser and validation regressions for the expanded DSL.
- Modify: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
  Responsibility: runtime regression coverage for the shared semantics through the agent runtime.
- Modify: `src/OpenClaw.Tests/MafAdapterTests.cs`
  Responsibility: parity tests for the MAF runtime.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
  Responsibility: document implemented P0 parity and remaining non-goals.
- Modify: `docs/opensquilla-meta-skill-migration.md`
  Responsibility: keep the English migration note aligned.

### New files to create

- Create: `src/OpenClaw.Core/Skills/Meta/MetaExecutionContext.cs`
  Responsibility: canonical template and condition evaluation context.
- Create: `src/OpenClaw.Core/Skills/Meta/MetaTemplateRenderer.cs`
  Responsibility: Jinja2.NET wrapper plus OpenSquilla-compatible filters.
- Create: `src/OpenClaw.Core/Skills/Meta/MetaConditionEvaluator.cs`
  Responsibility: evaluate `when` and route-array conditions using shared truthiness rules.
- Create: `src/OpenClaw.Core/Skills/Meta/MetaToolArgumentResolver.cs`
  Responsibility: merge composition `tool_args`, step `with`, and step `tool_args`, then render and validate final JSON object arguments.
- Create: `src/OpenClaw.Core/Skills/Meta/MetaClarifyValidator.cs`
  Responsibility: validate chat/form clarify input, defaults, cancel words, timeout semantics, and canonical JSON output.
- Create: `src/OpenClaw.Core/Skills/Meta/MetaRoutePlanner.cs`
  Responsibility: compute initial pending/blocked route targets and select the winning route-array branch.
- Create: `src/OpenClaw.Tests/MetaCoreServicesTests.cs`
  Responsibility: unit tests for Jinja rendering, conditions, tool args, clarify validation, and route evaluation.

### Validation commands to use throughout

- Parser-only verification: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillLoaderTests"`
- Core-service verification: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MetaCoreServicesTests"`
- Agent runtime verification: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~AgentRuntimeTests.ExecuteMetaSkillAsync"`
- MAF runtime verification: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ExecuteMetaSkillAsync"`
- Compile safety net: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`
- Final verification: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj`

### Task 1: Add Core Package And DSL Types

**Files:**

- Modify: `src/OpenClaw.Core/OpenClaw.Core.csproj`
- Modify: `src/OpenClaw.Core/Skills/SkillModels.cs`
- Test: `src/OpenClaw.Tests/SkillTests.cs`

- [x] **Step 1: Write the failing parser/model test for the new typed fields**

```csharp
[Fact]
public void ParseSkillContent_MetaOpenSquillaDslFields_ParsesSuccessfully()
{
    var content = """
        ---
        name: meta-opensquilla-native
        description: Native DSL fields
        kind: meta
        composition: {
          "tool_args": { "trace_id": "{{ input }}", "format": "json" },
          "steps": [
            {
              "id": "collect",
              "kind": "user_input",
              "clarify": {
                "mode": "form",
                "fields": [
                  { "name": "topic", "type": "string", "required": true, "min_length": 3 },
                  { "name": "priority", "type": "enum", "options": ["low", "medium", "high"], "default": "medium" }
                ],
                "cancel_words": ["cancel"],
                "timeout_seconds": 30
              },
              "route": [
                { "when": "outputs.collect == 'bug'", "to": "bug_branch" },
                { "to": "default_branch" }
              ]
            },
            {
              "id": "bug_branch",
              "kind": "tool_call",
              "tool": "dispatch_bug",
              "tool_allowlist": ["dispatch_bug"],
              "tool_args": { "ticket": "{{ outputs.collect }}" },
              "output_choices": ["accepted", "rejected"]
            },
            {
              "id": "default_branch",
              "kind": "llm_chat",
              "when": "outputs.collect != ''"
            }
          ]
        }
        ---
        Meta instructions.
        """;

    var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-opensquilla-native", SkillSource.Workspace);

    Assert.NotNull(skill);
    Assert.Equal("{\"trace_id\":\"{{ input }}\",\"format\":\"json\"}", skill!.Composition!.ToolArgsJson);
    Assert.Equal("form", skill.Composition.Steps[0].Clarify!.Mode);
    Assert.Equal("outputs.collect != ''", skill.Composition.Steps[2].When);
    Assert.Equal(["dispatch_bug"], skill.Composition.Steps[1].ToolAllowlist);
    Assert.Equal(["accepted", "rejected"], skill.Composition.Steps[1].OutputChoices);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ParseSkillContent_MetaOpenSquillaDslFields_ParsesSuccessfully"`
Expected: FAIL because `MetaSkillComposition` and `MetaSkillStepDefinition` do not expose `ToolArgsJson`, `Clarify`, `When`, `ToolAllowlist`, `OutputChoices`, or route-array types yet.

- [x] **Step 3: Add the dependency and new typed models**

```xml
<ItemGroup>
  <PackageReference Include="Jinja2.NET" Version="1.4.1" />
  <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.3.0" />
  <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="10.0.3" />
  <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.3" />
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.3" />
  <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.7" />
  <PackageReference Include="Spectre.Console" Version="0.54.0" />
  <PackageReference Include="NCrontab" Version="3.3.0" />
  <PackageReference Include="TickerQ" Version="10.3.0" />
</ItemGroup>
```

```csharp
public sealed class MetaSkillComposition
{
    public string? ToolArgsJson { get; init; }
    public IReadOnlyList<MetaSkillStepDefinition> Steps { get; init; } = [];
}

public sealed class MetaSkillStepDefinition
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? Skill { get; init; }
    public string? Tool { get; init; }
    public string? WithJson { get; init; }
    public string? When { get; init; }
    public string? ToolArgsJson { get; init; }
    public IReadOnlyList<string> ToolAllowlist { get; init; } = [];
    public IReadOnlyList<string> OutputChoices { get; init; } = [];
    public MetaClarifySchema? Clarify { get; init; }
    public IReadOnlyList<MetaRouteDefinition> Routes { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public string? OnFailure { get; init; }
    public int? TimeoutSeconds { get; init; }
    public MetaStepRetryPolicy Retry { get; init; } = new();
    public MetaStepOutputContract OutputContract { get; init; } = new();
}

public sealed class MetaRouteDefinition
{
    public string? When { get; init; }
    public required string To { get; init; }
}

public sealed class MetaClarifySchema
{
    public string Mode { get; init; } = "chat";
    public IReadOnlyList<MetaClarifyField> Fields { get; init; } = [];
    public IReadOnlyList<string> CancelWords { get; init; } = [];
    public int? TimeoutSeconds { get; init; }
    public bool ExtractNaturalLanguage { get; init; }
}

public sealed class MetaClarifyField
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Required { get; init; }
    public object? DefaultValue { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public double? Min { get; init; }
    public double? Max { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ParseSkillContent_MetaOpenSquillaDslFields_ParsesSuccessfully"`
Expected: PASS

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: add meta skill DSL models`

### Task 2: Extend The Parser And Fail-Fast Validation

**Files:**

- Modify: `src/OpenClaw.Core/Skills/SkillLoader.cs`
- Modify: `src/OpenClaw.Tests/SkillTests.cs`
- Test: `src/OpenClaw.Tests/SkillTests.cs`

- [x] **Step 1: Write failing parser diagnostics for invalid native DSL cases**

```csharp
[Theory]
[InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"search\",\"tool_allowlist\":[]}]}", "invalid_tool_allowlist")]
[InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"mode\":\"form\",\"fields\":[]}}]}", "invalid_clarify_schema")]
[InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"to\":\"missing\"}]}]}", "invalid_route_target")]
[InlineData("{\"steps\":[{\"id\":\"chat\",\"kind\":\"llm_chat\",\"when\":1}]}", "invalid_when_expression")]
[InlineData("{\"tool_args\":[],\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"search\"}]}", "invalid_tool_args")]
public void TryParseSkillContent_MetaNativeDslDiagnostics_ReturnsExpectedCode(string compositionJson, string expectedError)
{
    var content = $$"""
        ---
        name: meta-native-diagnostics
        description: Invalid native fields
        kind: meta
        composition: {{compositionJson}}
        ---
        Meta instructions.
        """;

    var ok = SkillLoader.TryParseSkillContent(content, "/skills/meta-native-diagnostics", SkillSource.Workspace, out var skill, out var errorCode);

    Assert.False(ok);
    Assert.Null(skill);
    Assert.Equal(expectedError, errorCode);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~TryParseSkillContent_MetaNativeDslDiagnostics_ReturnsExpectedCode"`
Expected: FAIL because `SkillLoader.ParseComposition` does not yet parse or validate the new fields.

- [x] **Step 3: Implement the parser and validation helpers**

```csharp
if (doc.RootElement.TryGetProperty("tool_args", out var compositionToolArgs))
{
    if (compositionToolArgs.ValueKind != JsonValueKind.Object)
    {
        errorCode = "invalid_tool_args";
        return null;
    }

    compositionToolArgsJson = compositionToolArgs.GetRawText();
}

if (stepElement.TryGetProperty("when", out var whenElement))
{
    if (whenElement.ValueKind != JsonValueKind.String)
    {
        errorCode = "invalid_when_expression";
        return null;
    }

    when = whenElement.GetString();
}

if (!TryParseToolAllowlist(stepElement, kind, out var toolAllowlist, out errorCode) ||
    !TryParseOutputChoices(stepElement, out var outputChoices, out errorCode) ||
    !TryParseClarify(stepElement, kind, out var clarify, out errorCode) ||
    !TryParseRouteArray(stepElement, out var routes, out var hasRouteArray, out errorCode))
{
    return null;
}

if (hasRouteArray && HasLegacyRouteObject(withJson))
{
    errorCode = "invalid_route";
    return null;
}

steps.Add(new MetaSkillStepDefinition
{
    Id = idElement.GetString()!,
    Kind = kind,
    Skill = skill,
    Tool = tool,
    WithJson = withJson,
    When = when,
    ToolArgsJson = stepToolArgsJson,
    ToolAllowlist = toolAllowlist,
    OutputChoices = outputChoices,
    Clarify = clarify,
    Routes = routes,
    DependsOn = dependsOn,
    OnFailure = onFailure,
    TimeoutSeconds = timeoutSeconds,
    Retry = retry,
    OutputContract = outputContract
});

return new MetaSkillComposition
{
    ToolArgsJson = compositionToolArgsJson,
    Steps = steps
};
```

```csharp
private static bool ValidateRouteArrays(IReadOnlyList<MetaSkillStepDefinition> steps, out string? errorCode)
{
    errorCode = null;
    var ids = steps.Select(static step => step.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var step in steps)
    {
        var fallbackCount = 0;
        foreach (var route in step.Routes)
        {
            if (!ids.Contains(route.To))
            {
                errorCode = "invalid_route_target";
                return false;
            }

            if (string.Equals(route.To, step.Id, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "invalid_route_scope";
                return false;
            }

            if (string.IsNullOrWhiteSpace(route.When) && ++fallbackCount > 1)
            {
                errorCode = "invalid_route_fallback";
                return false;
            }
        }
    }

    return true;
}
```

- [x] **Step 4: Run parser verification**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillLoaderTests"`
Expected: PASS

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: validate opensquilla native meta fields`

### Task 3: Add Shared Jinja, Condition, Route, Tool-Args, And Clarify Services

**Files:**

- Create: `src/OpenClaw.Core/Skills/Meta/MetaExecutionContext.cs`
- Create: `src/OpenClaw.Core/Skills/Meta/MetaTemplateRenderer.cs`
- Create: `src/OpenClaw.Core/Skills/Meta/MetaConditionEvaluator.cs`
- Create: `src/OpenClaw.Core/Skills/Meta/MetaToolArgumentResolver.cs`
- Create: `src/OpenClaw.Core/Skills/Meta/MetaClarifyValidator.cs`
- Create: `src/OpenClaw.Core/Skills/Meta/MetaRoutePlanner.cs`
- Create: `src/OpenClaw.Tests/MetaCoreServicesTests.cs`
- Test: `src/OpenClaw.Tests/MetaCoreServicesTests.cs`

- [x] **Step 1: Write the failing Core-service tests**

```csharp
public sealed class MetaCoreServicesTests
{
    [Fact]
    public void MetaTemplateRenderer_RendersOutputsAndFilters()
    {
        var renderer = new MetaTemplateRenderer();
        var context = new MetaExecutionContext(
            input: "Need <xml>",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["step-id"] = "Quarterly Report",
                ["summary"] = "This is a very long answer that should be shortened for prompt safety."
            });

        var rendered = renderer.Render("{{ input|xml_escape }} {{ outputs[\"step-id\"]|slugify }} {{ outputs.summary|truncate(12) }}", context);

        Assert.Equal("Need &lt;xml&gt; quarterly-report This is a...", rendered);
    }

    [Theory]
    [InlineData("{{ outputs.classify == 'bug' }}", true)]
    [InlineData("outputs.classify == 'doc'", false)]
    [InlineData("off", false)]
    [InlineData("1", true)]
    public void MetaConditionEvaluator_UsesSharedTruthiness(string expression, bool expected)
    {
        var context = new MetaExecutionContext(
            input: "hello",
            outputs: new Dictionary<string, string> { ["classify"] = "bug" });

        var evaluator = new MetaConditionEvaluator(new MetaTemplateRenderer());

        Assert.Equal(expected, evaluator.Evaluate(expression, context));
    }

    [Fact]
    public void MetaToolArgumentResolver_MergesAndRendersJsonObject()
    {
        var resolver = new MetaToolArgumentResolver(new MetaTemplateRenderer());
        var context = new MetaExecutionContext(
            input: "incident-42",
            outputs: new Dictionary<string, string> { ["prepare"] = "ready" });

        var result = resolver.Resolve(
            "{\"trace\":\"{{ input }}\",\"mode\":\"default\"}",
            "{\"mode\":\"with\",\"state\":\"{{ outputs.prepare }}\"}",
            "{\"mode\":\"step\"}",
            context);

        Assert.Equal("{\"trace\":\"incident-42\",\"mode\":\"step\",\"state\":\"ready\"}", result);
    }

    [Fact]
    public void MetaClarifyValidator_NormalizesFormInputToCanonicalJson()
    {
        var schema = new MetaClarifySchema
        {
            Mode = "form",
            Fields =
            [
                new MetaClarifyField { Name = "topic", Type = "string", Required = true, MinLength = 3 },
                new MetaClarifyField { Name = "priority", Type = "enum", Options = ["low", "medium", "high"], DefaultValue = "medium" }
            ]
        };

        var validator = new MetaClarifyValidator();
        var result = validator.ValidateAndNormalize("{\"topic\":\"OpenSquilla\"}", schema);

        Assert.True(result.IsValid);
        Assert.Equal("{\"topic\":\"OpenSquilla\",\"priority\":\"medium\"}", result.NormalizedOutput);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MetaCoreServicesTests"`
Expected: FAIL because the shared Core services do not exist yet.

- [x] **Step 3: Implement the shared Core services**

```csharp
public sealed class MetaExecutionContext
{
    public MetaExecutionContext(string input, IReadOnlyDictionary<string, string>? outputs = null)
    {
        Input = input;
        Inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user_message"] = input
        };
        Outputs = outputs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Steps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    public string Input { get; }
    public IReadOnlyDictionary<string, object?> Inputs { get; }
    public IReadOnlyDictionary<string, string> Outputs { get; }
    public IReadOnlyDictionary<string, object?> Steps { get; }
}
```

```csharp
public sealed class MetaTemplateRenderer
{
    public string Render(string template, MetaExecutionContext context)
    {
        var environment = new Environment();
        environment.Filters["xml_escape"] = value => SecurityElement.Escape(value?.ToString() ?? string.Empty);
        environment.Filters["slugify"] = value => Slugify(value?.ToString() ?? string.Empty);
        environment.Filters["truncate"] = (value, length) => Truncate(value?.ToString() ?? string.Empty, length is int size ? size : 80);
        environment.Filters["tojson"] = value => JsonSerializer.Serialize(value);

        var templateSource = environment.FromString(template);
        return templateSource.Render(new
        {
            input = context.Input,
            inputs = context.Inputs,
            outputs = context.Outputs,
            steps = context.Steps
        });
    }
}
```

```csharp
public sealed class MetaConditionEvaluator
{
    private readonly MetaTemplateRenderer _renderer;

    public MetaConditionEvaluator(MetaTemplateRenderer renderer) => _renderer = renderer;

    public bool Evaluate(string expression, MetaExecutionContext context)
    {
        var template = expression.Contains("{{", StringComparison.Ordinal) || expression.Contains("{%", StringComparison.Ordinal)
            ? expression
            : $"{{{{ {expression} }}}}";

        var rendered = _renderer.Render(template, context).Trim();
        return rendered.Length > 0 && rendered.ToLowerInvariant() switch
        {
            "false" or "no" or "0" or "off" or "null" or "none" => false,
            _ => true
        };
    }
}
```

```csharp
public sealed class MetaToolArgumentResolver
{
    private readonly MetaTemplateRenderer _renderer;

    public MetaToolArgumentResolver(MetaTemplateRenderer renderer) => _renderer = renderer;

    public string Resolve(string? compositionToolArgsJson, string? withJson, string? stepToolArgsJson, MetaExecutionContext context)
    {
        var merged = MergeObjects(compositionToolArgsJson, withJson, stepToolArgsJson);
        var rendered = _renderer.Render(merged, context);
        using var doc = JsonDocument.Parse(rendered);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("invalid_tool_args");

        return doc.RootElement.GetRawText();
    }
}
```

```csharp
public sealed class MetaClarifyValidator
{
    public MetaClarifyValidationResult ValidateAndNormalize(string? rawInput, MetaClarifySchema schema)
    {
        if (MatchesCancelWord(rawInput, schema.CancelWords))
            return MetaClarifyValidationResult.Failed("user_input_cancelled", "User cancelled input.");

        var normalized = Normalize(rawInput, schema);
        return ValidateFields(normalized, schema);
    }
}
```

- [x] **Step 4: Run the Core-service tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MetaCoreServicesTests"`
Expected: PASS

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: add shared meta skill core services`

### Task 4: Integrate Shared Services Into AgentRuntime

**Files:**

- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Modify: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeTests.cs`

- [x] **Step 1: Write failing agent-runtime tests for `when`, route arrays, tool args, and clarify**

```csharp
[Fact]
public async Task ExecuteMetaSkillAsync_WhenFalse_SkipsStepAndBlocksDependents()
{
    var tool = new CountingTool("chosen_tool", "chosen");
    var agent = CreateMetaAgent(tool, new MetaSkillComposition
    {
        Steps =
        [
            new MetaSkillStepDefinition { Id = "prepare", Kind = "user_input", WithJson = "{\"default\":\"skip\"}" },
            new MetaSkillStepDefinition { Id = "branch", Kind = "tool_call", Tool = "chosen_tool", When = "outputs.prepare == 'run'", DependsOn = ["prepare"] },
            new MetaSkillStepDefinition { Id = "after", Kind = "tool_call", Tool = "chosen_tool", DependsOn = ["branch"] }
        ]
    });

    var result = await InvokeMetaSkillAsync(agent, new Session { Id = "sess", SenderId = "u", ChannelId = "c" }, "meta-flow", "hello", CancellationToken.None);

    Assert.Equal(string.Empty, result);
    Assert.Equal(0, tool.CallCount);
}

[Fact]
public async Task ExecuteMetaSkillAsync_RouteArray_SelectsFirstMatchingBranch()
{
    var chosen = new CountingTool("chosen_tool", "chosen");
    var skipped = new CountingTool("skipped_tool", "skipped");
    var agent = CreateMetaAgent([chosen, skipped], new MetaSkillComposition
    {
        Steps =
        [
            new MetaSkillStepDefinition { Id = "ask", Kind = "user_input", WithJson = "{\"default\":\"bug\"}", Routes = [ new MetaRouteDefinition { When = "outputs.ask == 'bug'", To = "chosen" }, new MetaRouteDefinition { To = "skipped" } ] },
            new MetaSkillStepDefinition { Id = "chosen", Kind = "tool_call", Tool = "chosen_tool" },
            new MetaSkillStepDefinition { Id = "skipped", Kind = "tool_call", Tool = "skipped_tool" }
        ]
    }, finalTextMode: "step:chosen");

    var result = await InvokeMetaSkillAsync(agent, new Session { Id = "sess", SenderId = "u", ChannelId = "c" }, "meta-flow", "hello", CancellationToken.None);

    Assert.Equal("chosen", result);
    Assert.Equal(1, chosen.CallCount);
    Assert.Equal(0, skipped.CallCount);
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~AgentRuntimeTests.ExecuteMetaSkillAsync_WhenFalse|FullyQualifiedName~AgentRuntimeTests.ExecuteMetaSkillAsync_RouteArray"`
Expected: FAIL because `AgentRuntime` still uses local parsing/execution logic only.

- [x] **Step 3: Replace local helpers with Core services**

```csharp
var templateRenderer = new MetaTemplateRenderer();
var conditionEvaluator = new MetaConditionEvaluator(templateRenderer);
var toolArgumentResolver = new MetaToolArgumentResolver(templateRenderer);
var clarifyValidator = new MetaClarifyValidator();
var routePlanner = new MetaRoutePlanner(conditionEvaluator);

var metaContext = new MetaExecutionContext(input, outputs);

if (!string.IsNullOrWhiteSpace(step.When) && !conditionEvaluator.Evaluate(step.When, metaContext))
{
    pending.Remove(step.Id);
    BlockStepAndDependents(step.Id, blocked, pending, dependentsByStep);
    stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, "skipped", "condition_false", 0, false));
    continue;
}

if (NormalizeMetaStepKind(step.Kind) == "tool_call")
{
    if (step.ToolAllowlist.Count > 0 && !step.ToolAllowlist.Contains(step.Tool!, StringComparer.OrdinalIgnoreCase))
    {
        return BuildStructuredMetaExecutionJson(metaSkill.Name, string.Empty, stepResults, $"Meta step '{step.Id}' tool '{step.Tool}' is not allowlisted.");
    }

    var toolArgsJson = toolArgumentResolver.Resolve(
        metaSkill.Composition?.ToolArgsJson,
        step.WithJson,
        step.ToolArgsJson,
        metaContext);
}

routePlanner.ApplyInitialRoutingBlocks(steps, blocked, pending);
routePlanner.ApplyCompletionRouting(step, metaContext, stepById, blocked, pending, dependentsByStep);
```

- [x] **Step 4: Run the scoped agent-runtime verification**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~AgentRuntimeTests.ExecuteMetaSkillAsync"`
Expected: PASS

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: wire agent runtime to shared meta services`

### Task 5: Integrate Shared Services Into MafAgentRuntime

**Files:**

- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- Modify: `src/OpenClaw.Tests/MafAdapterTests.cs`
- Test: `src/OpenClaw.Tests/MafAdapterTests.cs`

- [x] **Step 1: Write the failing MAF parity tests**

```csharp
[Fact]
public async Task MafAgentRuntime_ExecuteMetaSkillAsync_WhenFalse_SkipsStepAndBlocksDependents()
{
    var runtime = CreateRuntime(
        storagePath: CreateTempStoragePath(),
        llmService: new TestLlmExecutionService(),
        options: new MafOptions(),
        tools: [new CountingMafTool("chosen_tool", "chosen")],
        skills:
        [
            new SkillDefinition
            {
                Name = "meta-flow",
                Description = "meta flow",
                Instructions = "...",
                Location = "/skills/meta-flow",
                Kind = SkillKind.Meta,
                Composition = new MetaSkillComposition
                {
                    Steps =
                    [
                        new MetaSkillStepDefinition { Id = "prepare", Kind = "user_input", WithJson = "{\"default\":\"skip\"}" },
                        new MetaSkillStepDefinition { Id = "branch", Kind = "tool_call", Tool = "chosen_tool", When = "outputs.prepare == 'run'", DependsOn = ["prepare"] }
                    ]
                }
            }
        ]);

    var result = await InvokeMafMetaSkillAsync(runtime, CreateSession("maf-when-false"), "meta-flow", "hello", CancellationToken.None);

    Assert.Equal(string.Empty, result);
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ExecuteMetaSkillAsync_WhenFalse"`
Expected: FAIL because the MAF runtime has not been switched to the shared services.

- [x] **Step 3: Port the same shared-service calls into the MAF runtime**

```csharp
var templateRenderer = new MetaTemplateRenderer();
var conditionEvaluator = new MetaConditionEvaluator(templateRenderer);
var toolArgumentResolver = new MetaToolArgumentResolver(templateRenderer);
var clarifyValidator = new MetaClarifyValidator();
var routePlanner = new MetaRoutePlanner(conditionEvaluator);

var metaContext = new MetaExecutionContext(input, outputs);

if (!string.IsNullOrWhiteSpace(step.When) && !conditionEvaluator.Evaluate(step.When, metaContext))
{
    pending.Remove(step.Id);
    BlockStepAndDependents(step.Id, blocked, pending, dependentsByStep);
    stepResults.Add(new MetaStepExecutionResult(step.Id, step.Kind, "skipped", "condition_false", 0, false));
    continue;
}

var toolArgsJson = toolArgumentResolver.Resolve(
    metaSkill.Composition?.ToolArgsJson,
    step.WithJson,
    step.ToolArgsJson,
    metaContext);
```

- [x] **Step 4: Run the scoped MAF-runtime verification**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ExecuteMetaSkillAsync"`
Expected: PASS

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: wire maf runtime to shared meta services`

### Task 6: Tighten Output Choice, Clarify Cancel/Timeout, And Template Failure Semantics

**Files:**

- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- Modify: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Modify: `src/OpenClaw.Tests/MafAdapterTests.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Test: `src/OpenClaw.Tests/MafAdapterTests.cs`

- [x] **Step 1: Add the failing runtime regression tests for error-code semantics**

```csharp
[Fact]
public async Task ExecuteMetaSkillAsync_ClarifyCancel_ReturnsStructuredCancelError()
{
    var agent = CreateMetaAgent([], new MetaSkillComposition
    {
        Steps =
        [
            new MetaSkillStepDefinition
            {
                Id = "ask",
                Kind = "user_input",
                Clarify = new MetaClarifySchema
                {
                    Mode = "chat",
                    Fields = [ new MetaClarifyField { Name = "topic", Type = "string", Required = true } ],
                    CancelWords = ["cancel"]
                }
            }
        ]
    }, finalTextMode: "structured");

    var result = await InvokeMetaSkillAsync(agent, new Session { Id = "sess", SenderId = "u", ChannelId = "c" }, "meta-flow", "cancel", CancellationToken.None);

    using var doc = JsonDocument.Parse(result);
    Assert.Equal("user_input_cancelled", doc.RootElement.GetProperty("error_code").GetString());
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ClarifyCancel|FullyQualifiedName~template_render_failed|FullyQualifiedName~invalid_output_choice"`
Expected: FAIL because the runtimes do not yet map all new failure modes.

- [x] **Step 3: Implement the remaining runtime semantics**

```csharp
if (step.OutputChoices.Count > 0)
{
    var candidate = output.Trim();
    if (!step.OutputChoices.Contains(candidate, StringComparer.OrdinalIgnoreCase))
    {
        failureCode = "invalid_output_choice";
        return false;
    }
}

try
{
    rendered = templateRenderer.Render(template, metaContext);
}
catch (Exception ex)
{
    return BuildStructuredMetaExecutionJson(metaSkill.Name, string.Empty, stepResults, $"Meta step '{step.Id}' template render failed: {ex.Message}");
}

if (clarifyResult.FailureCode is "user_input_cancelled" or "user_input_timeout")
{
    if (TryActivateFailureBranch(step, stepById, pending, blocked, failureAliases))
        continue;

    return BuildStructuredMetaExecutionJson(metaSkill.Name, string.Empty, stepResults, clarifyResult.FailureMessage);
}
```

- [x] **Step 4: Run the focused runtime checks again**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ExecuteMetaSkillAsync_Clarify|FullyQualifiedName~MafAgentRuntime_ExecuteMetaSkillAsync_Clarify|FullyQualifiedName~invalid_output_choice|FullyQualifiedName~template_render_failed"`
Expected: PASS

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: complete meta clarify and output enforcement`

### Task 7: Update Migration Documentation And Call Out Remaining Non-Goals

**Files:**

- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
- Modify: `docs/opensquilla-meta-skill-migration.md`

- [x] **Step 1: Write the failing doc assertion by deciding the exact statements that must change**

```markdown
- P0 native DSL compatibility is now implemented for `output_choices`, composition/step `tool_args`, step `tool_allowlist`, `clarify`, `when`, and route arrays.
- `user_input.clarify` is now a typed parser/runtime contract with chat/form JSON resume semantics.
- Jinja rendering now uses `Jinja2.NET 1.4.1` with `xml_escape`, `slugify`, `truncate`, and `tojson`.
- Visual form UI, `skill_exec` subprocess semantics, run history, replay, and parallel scheduling remain future work.
```

- [x] **Step 2: Update both migration docs**

```markdown
## Newly completed parity

- Native OpenSquilla DSL fields are now first-class parser/runtime contracts: `output_choices`, composition `tool_args`, step `tool_args`, `tool_allowlist`, `clarify`, `when`, and route arrays.
- `user_input.clarify` now validates typed chat/form input and normalizes successful multi-field results to canonical JSON text.
- Jinja rendering now uses `Jinja2.NET 1.4.1` with OpenSquilla-compatible `xml_escape`, `slugify`, `truncate`, and `tojson` filters.

## Still out of scope

- No visual form UI was added.
- `skill_exec` still does not run OpenSquilla-style subprocess entrypoints.
- Persistent meta-run history, replay tooling, and proposal flows remain future work.
- Parallel step scheduling remains future work.
```

- [x] **Step 3: Sanity-check the docs render cleanly**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`
Expected: PASS and no compile regressions from any doc-adjacent generated artifacts.

- [x] **Step 4: Record slice completion**

Suggested commit message if this slice is later committed separately: `docs: update opensquilla meta skill parity notes`

### Task 8: Final Verification And AOT/JIT Risk Check

**Files:**

- Modify: `src/OpenClaw.Core/OpenClaw.Core.csproj`
- Modify: `src/OpenClaw.Core/Skills/Meta/MetaTemplateRenderer.cs`
- Test: `src/OpenClaw.Tests/SkillTests.cs`
- Test: `src/OpenClaw.Tests/MetaCoreServicesTests.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Test: `src/OpenClaw.Tests/MafAdapterTests.cs`

- [x] **Step 1: Run a full compile safety net**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`
Expected: PASS

- [x] **Step 2: Run the full test project**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj`
Expected: PASS

Observed on 2026-06-12: PASS. Full suite result: 1895 passed, 0 failed, 0 skipped.

- [x] **Step 3: If package-restore or trim behavior is suspect, run isolated restore/build checks**

Run: `dotnet restore src/OpenClaw.Core/OpenClaw.Core.csproj`
Expected: PASS

Run: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj -v minimal`
Expected: PASS

- [x] **Step 4: Review AOT/JIT implications before declaring done**

```text
- Confirm the Core-layer Jinja wrapper does not add reflection-based discovery code outside the package boundary.
- Confirm runtime behavior is shared through Core services, not duplicated divergence.
- Confirm docs state that Jinja parity is implemented while `skill_exec` subprocess parity is still future work.
```

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: complete opensquilla meta skill p0 and jinja parity`

## Self-Review

### Spec coverage

- P0 native DSL fields: covered by Task 1 and Task 2.
- Full `user_input.clarify` contract: covered by Task 1, Task 3, Task 4, and Task 6.
- Jinja2.NET 1.4.1 integration and filters: covered by Task 1 and Task 3.
- Shared Core ownership with both runtimes consuming it: covered by Task 3, Task 4, and Task 5.
- Route-array semantics, initial pending exclusions, and branch activation: covered by Task 2, Task 3, Task 4, and Task 5.
- Output choice, allowlist, template failure, cancel, and timeout error codes: covered by Task 2 and Task 6.
- Migration docs update and explicit non-goals: covered by Task 7.
- Verification commands: covered by Task 8.

### Placeholder scan

- No `TODO`, `TBD`, or deferred implementation placeholders remain.
- Every code-changing task includes a concrete snippet and an exact command.
- Every validation step names the file scope and expected outcome.

### Type consistency

- The plan consistently uses `MetaExecutionContext`, `MetaTemplateRenderer`, `MetaConditionEvaluator`, `MetaToolArgumentResolver`, `MetaClarifyValidator`, and `MetaRoutePlanner` across all tasks.
- DSL model property names match the spec and the parser/runtime tasks: `ToolArgsJson`, `ToolAllowlist`, `OutputChoices`, `Clarify`, `Routes`, and `When`.
- Error code names are consistent across parser, runtime, tests, and docs.
