# Meta-Runs Derived Proposals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only `openclaw skills meta-runs proposals` operator surface that derives candidate proposal summaries from persisted meta-run audit evidence without integrating `LearningProposal` storage.

**Architecture:** Extend the existing `meta-runs` CLI family with two read-only commands: a list surface and a show surface. Keep the data model local to `Session.cs` with small source-generated DTOs and shared constants, derive one candidate proposal for `paused` or `failed` runs only, and explicitly label every result as `derived_meta_run_evidence` so the later `LearningProposal` migration stays a compatible evolution instead of a semantic rewrite.

**Tech Stack:** .NET 10, `System.Text.Json`, xUnit, source-generated JSON via `CoreJsonContext`

**Implementation Status (2026-06-13):** Functional scope completed and validated in the current branch. This plan was executed inline in the active workspace without the per-slice commits suggested below. The shipped `meta-runs proposals show` detail surface now also expands an additive `evidence` summary, persisted `steps[]` evidence, and a structured `checkpoint` summary while remaining additive and read-only. The older top-level detail fields are still emitted as compatibility mirrors, but operator-facing consumers should prefer grouped `checkpoint` / `evidence` data.

---

## Worktree And Constraints

- Keep this slice read-only. Do not add `accept`, `reject`, `rollback`, or any other mutation workflow.
- Do not integrate `ILearningProposalStore`, `FileFeatureStore`, `SqliteFeatureStore`, or dashboard proposal models in this plan.
- Preserve the existing behavior of `meta-runs`, `meta-runs replay`, and `meta-runs reconstruct`.
- Keep all new JSON DTOs NativeAOT-safe and covered by `CoreJsonContext`.
- Derive proposals only from persisted session evidence already available on `Session`, `SessionMetaRunRecord`, and optional `SessionMetaExecutionCheckpoint`.

## File Structure

### Existing files to modify

- Modify: `src/OpenClaw.Core/Models/Session.cs`
  Responsibility: add derived-proposal DTOs, constants, and `CoreJsonContext` coverage.
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
  Responsibility: route `meta-runs proposals` and `meta-runs proposals show`, build deterministic derived proposal summaries/details, and print stable JSON/text output.
- Modify: `src/OpenClaw.Cli/Program.cs`
  Responsibility: surface the new proposal commands in top-level help.
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
  Responsibility: lock JSON/text contracts, parameter validation, and no-proposal cases for the derived proposal commands.
- Modify: `src/OpenClaw.Tests/CliProgramTests.cs`
  Responsibility: lock help text for the new proposal commands.
- Modify: `docs/opensquilla-meta-skill-migration.md`
  Responsibility: upgrade the migration note from “proposal workflow unimplemented” to “derived read-only proposal view shipped, durable lifecycle still pending”.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
  Responsibility: keep the Chinese migration note aligned with the shipped proposal surface and the future `LearningProposal` migration.

### Validation commands to use throughout

- Proposal slice: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals"`
- Help slice: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"`
- Meta-runs regression: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"`
- Compile safety net: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`

## Derivation Rules To Implement

- Only derive candidate proposals for `paused` or `failed` runs.
- Produce at most one proposal per run in this slice.
- Use deterministic proposal IDs:
  - paused run: `meta-run:<runId>:paused`
  - failed run: `meta-run:<runId>:failed`
- Use deterministic proposal kinds:
  - paused run: `paused_run_followup`
  - failed run: `failed_run_review`
- Use deterministic source value: `derived_meta_run_evidence`
- Use read-only available actions only:
  - list result: `show`
  - detail result: `show`
- Do not derive proposals for `completed` runs.

## Task 1: Add Derived Proposal DTOs And JSON Coverage

**Files:**

- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [x] **Step 1: Write the failing JSON list contract test**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Json_PrintsDerivedPausedAndFailedSummaries()
{
    var root = CreateTempRoot();
    var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
    var previousOut = Console.Out;
    var previousError = Console.Error;

    try
    {
        var workspace = Path.Combine(root, "workspace");
        var memoryPath = Path.Combine(root, "memory");
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

        await using (var store = new FileMemoryStore(memoryPath))
        {
            await store.SaveSessionAsync(new Session
            {
                Id = "sess-meta-proposals-json",
                ChannelId = "cli",
                SenderId = "tester",
                MetaRunHistory =
                {
                    new SessionMetaRunRecord
                    {
                        RunId = "run-paused-001",
                        SkillName = "meta-flow",
                        Status = "paused",
                        StepResults =
                        {
                            new SessionMetaStepResult { Id = "draft", Kind = "llm_chat", Status = "completed", DurationMs = 4 }
                        }
                    },
                    new SessionMetaRunRecord
                    {
                        RunId = "run-failed-001",
                        SkillName = "meta-flow",
                        Status = "failed",
                        ErrorCode = "tool_failed"
                    },
                    new SessionMetaRunRecord
                    {
                        RunId = "run-completed-001",
                        SkillName = "meta-flow",
                        Status = "completed",
                        FinalText = "done"
                    }
                },
                MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                {
                    SkillName = "meta-flow",
                    PendingStepId = "ask_user",
                    PendingStepIds = ["ask_user"],
                    BlockedStepIds = ["finalize"]
                }
            }, CancellationToken.None);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-json", "--storage", memoryPath, "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        using var document = JsonDocument.Parse(output.ToString());
        var rootElement = document.RootElement;
        Assert.Equal("sess-meta-proposals-json", rootElement.GetProperty("sessionId").GetString());
        Assert.Equal(2, rootElement.GetProperty("count").GetInt32());
        var proposals = rootElement.GetProperty("proposals");
        Assert.Equal("meta-run:run-paused-001:paused", proposals[0].GetProperty("id").GetString());
        Assert.Equal("paused_run_followup", proposals[0].GetProperty("kind").GetString());
        Assert.Equal("derived_meta_run_evidence", proposals[0].GetProperty("source").GetString());
        Assert.Equal("show", proposals[0].GetProperty("availableActions")[0].GetString());
        Assert.Equal("meta-run:run-failed-001:failed", proposals[1].GetProperty("id").GetString());
        Assert.Equal("failed_run_review", proposals[1].GetProperty("kind").GetString());
    }
    finally
    {
        Console.SetOut(previousOut);
        Console.SetError(previousError);
        Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
        Directory.Delete(root, recursive: true);
    }
}
```

- [x] **Step 2: Run the targeted test and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Json_PrintsDerivedPausedAndFailedSummaries"`

Expected: FAIL because the `proposals` subcommand, proposal DTOs, and generated JSON metadata do not exist yet.

- [x] **Step 3: Add the minimal DTOs and constants in `Session.cs`**

```csharp
public sealed class MetaRunDerivedProposalListResponse
{
    public required string SessionId { get; init; }
    public required int Count { get; init; }
    public MetaRunDerivedProposalSummary[] Proposals { get; init; } = [];
}

public sealed class MetaRunDerivedProposalSummary
{
    public required string Id { get; init; }
    public required string RunId { get; init; }
    public required string SkillName { get; init; }
    public required string Status { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public string Source { get; init; } = MetaRunProposalSources.DerivedMetaRunEvidence;
    public string[] AvailableActions { get; init; } = [MetaRunProposalActions.Show];
}

public sealed class MetaRunDerivedProposalDetailResponse
{
    public required string SessionId { get; init; }
    public required MetaRunDerivedProposalDetail Proposal { get; init; }
}

public sealed class MetaRunDerivedProposalDetail
{
    public required string Id { get; init; }
    public required string RunId { get; init; }
    public required string SkillName { get; init; }
    public required string Status { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public string Source { get; init; } = MetaRunProposalSources.DerivedMetaRunEvidence;
    public string[] AvailableActions { get; init; } = [MetaRunProposalActions.Show];
    public string? PendingStepId { get; init; }
    public string[] PendingStepIds { get; init; } = [];
    public string[] BlockedStepIds { get; init; } = [];
    public string[] TimelineStepIds { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public string? FinalText { get; init; }
}

public static class MetaRunProposalSources
{
    public const string DerivedMetaRunEvidence = "derived_meta_run_evidence";
}

public static class MetaRunProposalActions
{
    public const string Show = "show";
}

public static class MetaRunProposalKinds
{
    public const string PausedRunFollowup = "paused_run_followup";
    public const string FailedRunReview = "failed_run_review";
}
```

Also add generated JSON coverage near the existing replay annotations:

```csharp
[JsonSerializable(typeof(MetaRunDerivedProposalListResponse))]
[JsonSerializable(typeof(MetaRunDerivedProposalSummary))]
[JsonSerializable(typeof(MetaRunDerivedProposalSummary[]))]
[JsonSerializable(typeof(MetaRunDerivedProposalDetailResponse))]
[JsonSerializable(typeof(MetaRunDerivedProposalDetail))]
```

- [x] **Step 4: Re-run the same test to confirm the failure moves to missing CLI behavior**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Json_PrintsDerivedPausedAndFailedSummaries"`

Expected: FAIL with a command-routing or output mismatch instead of a compile-time missing-type error.

- [ ] **Step 5: Commit the model-layer change**

```bash
git add src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat: add derived meta-run proposal contracts"
```

## Task 2: Route The Proposal Commands And Build Derived Results

**Files:**

- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [x] **Step 1: Add failing list/show command tests**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Show_Json_PrintsDerivedPausedDetail()
{
    // Arrange a paused run plus checkpoint, then call:
    // SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:paused", "--json"])
    // Assert detail fields:
    // - proposal.id == meta-run:run-paused-001:paused
    // - proposal.kind == paused_run_followup
    // - proposal.pendingStepId == ask_user
    // - proposal.timelineStepIds contains draft
}

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Text_PrintsDerivedList()
{
    // Call:
    // SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-text", "--storage", memoryPath])
    // Assert text contains:
    // - Session: sess-meta-proposals-text
    // - Derived proposals: 2
    // - Proposal: meta-run:run-paused-001:paused
    // - Source: derived_meta_run_evidence
    // - Available actions: show
}

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Show_MissingProposal_ReturnsUsage()
{
    // Call without --proposal and assert exitCode == 2 plus:
    // "--proposal <id> is required for meta-runs proposals show."
}
```

- [x] **Step 2: Run the focused proposal slice and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals"`

Expected: FAIL because the command branches and builder helpers are still missing.

- [x] **Step 3: Add the `proposals` branches and deterministic builders**

Add the `ListMetaRunsAsync` routing first:

```csharp
if (args.Length > 0 && string.Equals(args[0], "proposals", StringComparison.OrdinalIgnoreCase))
    return await HandleMetaRunProposalsAsync(args.Skip(1).ToArray());
```

Then add a small dispatcher and builders:

```csharp
private static async Task<int> HandleMetaRunProposalsAsync(string[] args)
{
    if (args.Length > 0 && string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
        return await ShowMetaRunProposalAsync(args.Skip(1).ToArray());

    return await ListMetaRunProposalsAsync(args);
}

private static MetaRunDerivedProposalSummary[] BuildDerivedProposals(Session session, string? requestedRunId)
    => [..
        session.MetaRunHistory
            .Where(run => string.IsNullOrWhiteSpace(requestedRunId) || string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal))
            .Select(run => TryBuildDerivedProposalSummary(run, session.MetaExecutionCheckpoint))
            .Where(static proposal => proposal is not null)
            .Cast<MetaRunDerivedProposalSummary>()
            .OrderBy(static proposal => proposal.RunId, StringComparer.Ordinal)
    ];

private static MetaRunDerivedProposalSummary? TryBuildDerivedProposalSummary(
    SessionMetaRunRecord run,
    SessionMetaExecutionCheckpoint? checkpoint)
{
    if (string.Equals(run.Status, "paused", StringComparison.OrdinalIgnoreCase))
    {
        var pendingStepId = string.Equals(checkpoint?.SkillName, run.SkillName, StringComparison.OrdinalIgnoreCase)
            ? checkpoint?.PendingStepId
            : null;

        return new MetaRunDerivedProposalSummary
        {
            Id = $"meta-run:{run.RunId}:paused",
            RunId = run.RunId,
            SkillName = run.SkillName,
            Status = run.Status,
            Kind = MetaRunProposalKinds.PausedRunFollowup,
            Title = $"Resume paused meta run {run.SkillName}",
            Summary = string.IsNullOrWhiteSpace(pendingStepId)
                ? $"Run {run.RunId} is paused and needs operator follow-up."
                : $"Run {run.RunId} is paused at step {pendingStepId}."
        };
    }

    if (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase))
    {
        return new MetaRunDerivedProposalSummary
        {
            Id = $"meta-run:{run.RunId}:failed",
            RunId = run.RunId,
            SkillName = run.SkillName,
            Status = run.Status,
            Kind = MetaRunProposalKinds.FailedRunReview,
            Title = $"Review failed meta run {run.SkillName}",
            Summary = string.IsNullOrWhiteSpace(run.ErrorCode)
                ? $"Run {run.RunId} failed and needs review."
                : $"Run {run.RunId} failed with {run.ErrorCode}."
        };
    }

    return null;
}
```

Add show-detail parsing by looking up a derived summary first, then expanding detail from the selected run:

```csharp
private static MetaRunDerivedProposalDetail BuildDerivedProposalDetail(
    MetaRunDerivedProposalSummary summary,
    SessionMetaRunRecord run,
    SessionMetaExecutionCheckpoint? checkpoint)
{
    var checkpointMatches = string.Equals(run.Status, "paused", StringComparison.OrdinalIgnoreCase)
        && checkpoint is not null
        && string.Equals(checkpoint.SkillName, run.SkillName, StringComparison.OrdinalIgnoreCase);

    return new MetaRunDerivedProposalDetail
    {
        Id = summary.Id,
        RunId = summary.RunId,
        SkillName = summary.SkillName,
        Status = summary.Status,
        Kind = summary.Kind,
        Title = summary.Title,
        Summary = summary.Summary,
        PendingStepId = checkpointMatches ? checkpoint!.PendingStepId : null,
        PendingStepIds = checkpointMatches ? [.. checkpoint!.PendingStepIds] : [],
        BlockedStepIds = checkpointMatches ? [.. checkpoint!.BlockedStepIds] : [],
        TimelineStepIds = [.. run.StepResults.Select(static step => step.Id)],
        ErrorCode = run.ErrorCode,
        Error = run.Error,
        FinalText = run.FinalText
    };
}
```

Post-implementation note: the shipped detail shape later expanded beyond this initial minimum. `meta-runs proposals show` now keeps the original additive top-level fields and also includes:

- `evidence` as a run-level persisted evidence summary (`timelineStepIds`, `errorCode`, `error`, `finalText`)
- `steps[]` with persisted step-level evidence (`id`, `kind`, `status`, `failureCode`, `durationMs`, `continued`)
- `checkpoint` as a structured paused-run summary (`pendingStepId`, pending/blocked step IDs, `promptPresent`, output step IDs, failure-alias step IDs)

These additions stayed within the original read-only boundary and did not add proposal lifecycle semantics.

Compatibility note: top-level `PendingStepId`, `PendingStepIds`, `BlockedStepIds`, `TimelineStepIds`, `ErrorCode`, `Error`, and `FinalText` remain in the shipped detail contract as legacy-compatible mirrors for existing consumers. New operator-facing integrations should prefer grouped `checkpoint` and `evidence` fields.

- [x] **Step 4: Re-run the focused proposal tests and make them pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals"`

Expected: PASS for list/show JSON and text cases, including the missing-`--proposal` parameter check.

- [ ] **Step 5: Commit the CLI proposal surface**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat: add derived meta-run proposal commands"
```

## Task 3: Text Output, Help, Docs, And Regression Coverage

**Files:**

- Modify: `src/OpenClaw.Cli/Program.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Tests/CliProgramTests.cs`
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

- [x] **Step 1: Add failing help and text contract assertions**

```csharp
[Fact]
public async Task Main_Help_ListsSkillsMetaRunsProposalsCommands()
{
    var previousOut = Console.Out;
    using var output = new StringWriter();
    try
    {
        Console.SetOut(output);

        var exitCode = await OpenClaw.Cli.Program.Main(["--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("openclaw skills meta-runs proposals <session-id> [--run <run-id>] [--storage <path>] [--json]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("openclaw skills meta-runs proposals show <session-id> --proposal <id> [--storage <path>] [--json]", output.ToString(), StringComparison.Ordinal);
    }
    finally
    {
        Console.SetOut(previousOut);
    }
}
```

In `SkillCommandsTests.cs`, add one text assertion that verifies the list output explicitly says `Derived proposals:` and never says `Proposal lifecycle:` or `Accept:`.

- [x] **Step 2: Run the help slice and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"`

Expected: FAIL because top-level help and `skills` help have not been updated with the proposal commands yet.

- [x] **Step 3: Update help text and migration docs**

Update `SkillCommands.PrintHelp()` usage lines:

```text
openclaw skills meta-runs proposals <session-id> [--run <run-id>] [--storage <path>] [--json]
openclaw skills meta-runs proposals show <session-id> --proposal <id> [--storage <path>] [--json]
```

Update the notes block with explicit semantics:

```text
- `meta-runs proposals` returns derived read-only proposal summaries from persisted meta-run evidence.
- `meta-runs proposals show` expands a single derived proposal without implying durable lifecycle state.
```

Update `Program.PrintHelp()` so the global help contains the same two command lines.

Update both migration docs so they say the shipped surface now includes a derived read-only proposal view, while durable lifecycle, provenance parity, and `LearningProposal` store backing remain future work.

- [x] **Step 4: Run regression and compile validation**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"
dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal
```

Expected: PASS with the existing list/replay/reconstruct tests still green and the new proposal/help tests added to the passing set.

- [ ] **Step 5: Commit the docs/help follow-up**

```bash
git add src/OpenClaw.Cli/Program.cs src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/CliProgramTests.cs docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md
git commit -m "docs: describe derived meta-run proposal workflow"
```

## Self-Review Checklist

- Spec coverage: the plan covers the new derived proposal list/show surface, shared constants and DTOs, read-only CLI semantics, help updates, and migration-doc synchronization.
- Placeholder scan: no `TODO`, `TBD`, or “implement later” steps remain.
- Type consistency: the plan uses one deterministic vocabulary throughout: `MetaRunDerivedProposal*` DTOs, `MetaRunProposalSources.DerivedMetaRunEvidence`, `MetaRunProposalActions.Show`, and `MetaRunProposalKinds.{PausedRunFollowup,FailedRunReview}`.
- Boundary check: every task explicitly avoids `LearningProposal` store integration, mutation workflows, and changes to replay/reconstruct semantics.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-13-meta-runs-derived-proposals.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
