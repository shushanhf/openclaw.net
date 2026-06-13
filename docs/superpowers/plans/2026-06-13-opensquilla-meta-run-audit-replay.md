# OpenSquilla Meta-Run Audit Replay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a real audit replay reconstruction workflow for persisted meta runs, keep the existing replay preview intact, and reserve non-mutating proposal summary space without implementing proposal acceptance.

**Architecture:** Keep replay preview and replay reconstruction as separate CLI contracts. Add replay-reconstruction DTOs and constants in `OpenClaw.Core`, then have `OpenClaw.Cli` build a replay result from `SessionMetaRunRecord` plus optional `SessionMetaExecutionCheckpoint` augmentation, with tests locking JSON/text contracts before formatter implementation.

**Tech Stack:** .NET 10, `System.Text.Json`, xUnit, source-generated JSON via `CoreJsonContext`

**Implementation Status (2026-06-13):** Functional scope completed and validated in the current branch. The dedicated-worktree expectation in this plan was not executed; implementation was carried out inline in the active workspace.

---

## Worktree And Constraints

- Create and use a dedicated worktree before Task 1.
- Preserve NativeAOT friendliness; all new replay DTOs must be source-generator-friendly and avoid reflection-dependent serialization.
- Do not change the meaning of existing `meta-runs replay` preview output.
- Do not implement true re-execution, tool/model recalls, or proposal acceptance in this plan.

## File Structure

### Existing files to modify

- Modify: `src/OpenClaw.Core/Models/Session.cs`
  Responsibility: add replay reconstruction DTOs, replay/proposal constants, and `CoreJsonContext` coverage.
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
  Responsibility: route `meta-runs reconstruct`, build replay reconstruction results, and print stable text/JSON output.
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
  Responsibility: add reconstruct JSON/text contract tests and parameter error tests.
- Modify: `src/OpenClaw.Tests/CliProgramTests.cs`
  Responsibility: lock CLI help text for the new reconstruct surface.
- Modify: `docs/opensquilla-meta-skill-migration.md`
  Responsibility: distinguish preview-only replay from audit reconstruction after implementation.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
  Responsibility: keep the Chinese migration note aligned with the shipped operator surface.

### Validation commands to use throughout

- Reconstruct slice: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Reconstruct_"`
- Meta-runs slice: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsCommand"`
- Compile safety net: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`

## Task 1: Add Replay Reconstruction DTOs And Serialization Coverage

**Files:**

- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [x] **Step 1: Write the failing reconstruct JSON contract test**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Reconstruct_Json_PrintsHistoryOnlyCompletedRun()
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
                Id = "sess-meta-reconstruct-json",
                ChannelId = "cli",
                SenderId = "tester",
                MetaRunHistory =
                {
                    new SessionMetaRunRecord
                    {
                        RunId = "run-reconstruct-001",
                        SkillName = "meta-flow",
                        Status = "completed",
                        FinalText = "done",
                        StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:00:00Z"),
                        CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:00:02Z"),
                        StepResults =
                        {
                            new SessionMetaStepResult
                            {
                                Id = "draft",
                                Kind = "llm_chat",
                                Status = "completed",
                                DurationMs = 8
                            }
                        }
                    }
                }
            }, CancellationToken.None);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-json", "--storage", memoryPath, "--run", "run-reconstruct-001", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        using var document = JsonDocument.Parse(output.ToString());
        var rootElement = document.RootElement;
        Assert.Equal("sess-meta-reconstruct-json", rootElement.GetProperty("sessionId").GetString());
        Assert.Equal("run-reconstruct-001", rootElement.GetProperty("runId").GetString());
        Assert.Equal(MetaRunReplayExecutionModes.AuditReconstruction, rootElement.GetProperty("mode").GetString());
        Assert.Equal(MetaRunReplayExecutionSources.HistoryOnly, rootElement.GetProperty("source").GetString());
        Assert.Equal("completed", rootElement.GetProperty("status").GetString());
        Assert.Equal("done", rootElement.GetProperty("finalText").GetString());
        Assert.Single(rootElement.GetProperty("timeline").EnumerateArray());
        Assert.False(rootElement.GetProperty("proposalSummary").GetProperty("available").GetBoolean());
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

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Reconstruct_Json_PrintsHistoryOnlyCompletedRun"`
Expected: FAIL because `reconstruct` is not routed yet and the replay reconstruction DTO/constants do not exist.

- [x] **Step 3: Add replay reconstruction DTOs, constants, and JSON context coverage**

```csharp
public sealed class MetaRunReplayResultResponse
{
    public required string SessionId { get; init; }
    public required string RunId { get; init; }
    public required string SkillName { get; init; }
    public string Mode { get; init; } = MetaRunReplayExecutionModes.AuditReconstruction;
    public required string Status { get; init; }
    public string Source { get; init; } = MetaRunReplayExecutionSources.HistoryOnly;
    public string? FinalText { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public MetaRunReplayTimelineItem[] Timeline { get; init; } = [];
    public MetaRunReplayCheckpointSummary? Checkpoint { get; init; }
    public MetaRunProposalSummary ProposalSummary { get; init; } = new();
}

public sealed class MetaRunReplayTimelineItem
{
    public int Sequence { get; init; }
    public required string StepId { get; init; }
    public required string Kind { get; init; }
    public required string Status { get; init; }
    public string? FailureCode { get; init; }
    public double DurationMs { get; init; }
    public bool Continued { get; init; }
    public string Source { get; init; } = MetaRunReplayTimelineSources.RunHistory;
    public string? Notes { get; init; }
}

public sealed class MetaRunReplayCheckpointSummary
{
    public required string PendingStepId { get; init; }
    public string[] PendingStepIds { get; init; } = [];
    public string[] BlockedStepIds { get; init; } = [];
    public bool PromptPresent { get; init; }
    public string[] OutputStepIds { get; init; } = [];
    public string[] FailureAliasStepIds { get; init; } = [];
}

public sealed class MetaRunProposalSummary
{
    public bool Available { get; init; }
    public int Count { get; init; }
    public string[] Kinds { get; init; } = [];
    public string Reason { get; init; } = MetaRunProposalReasons.NotImplemented;
}

public static class MetaRunReplayExecutionModes
{
    public const string AuditReconstruction = "audit_reconstruction";
}

public static class MetaRunReplayExecutionSources
{
    public const string HistoryOnly = "history_only";
    public const string HistoryPlusCheckpoint = "history_plus_checkpoint";
}

public static class MetaRunReplayTimelineSources
{
    public const string RunHistory = "run_history";
    public const string Checkpoint = "checkpoint";
}

public static class MetaRunProposalReasons
{
    public const string NotImplemented = "proposal_workflow_not_implemented";
}
```

```csharp
[JsonSerializable(typeof(MetaRunReplayResultResponse))]
[JsonSerializable(typeof(MetaRunReplayTimelineItem))]
[JsonSerializable(typeof(MetaRunReplayTimelineItem[]))]
[JsonSerializable(typeof(MetaRunReplayCheckpointSummary))]
[JsonSerializable(typeof(MetaRunProposalSummary))]
```

- [x] **Step 4: Run test to verify the model layer compiles into the JSON contract**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`
Expected: PASS for model compilation, while the reconstruct command test still fails because CLI routing is not implemented yet.

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: add meta run reconstruct models`

## Task 2: Add Reconstruct Routing And JSON Replay Assembly

**Files:**

- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [x] **Step 1: Write the failing paused-run checkpoint augmentation test**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Reconstruct_Json_PausedRun_UsesCheckpointAugmentation()
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
                Id = "sess-meta-reconstruct-paused",
                ChannelId = "cli",
                SenderId = "tester",
                MetaRunHistory =
                {
                    new SessionMetaRunRecord
                    {
                        RunId = "run-reconstruct-paused-001",
                        SkillName = "meta-flow",
                        Status = "paused",
                        StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:05:00Z"),
                        CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:05:02Z"),
                        StepResults =
                        {
                            new SessionMetaStepResult
                            {
                                Id = "draft",
                                Kind = "llm_chat",
                                Status = "completed",
                                DurationMs = 4
                            }
                        }
                    }
                },
                MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                {
                    SkillName = "meta-flow",
                    PendingStepId = "ask_user",
                    Prompt = "Need more detail",
                    PendingStepIds = ["ask_user"],
                    BlockedStepIds = ["finalize"],
                    Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["draft"] = "draft output"
                    },
                    FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["primary"] = "fallback"
                    }
                }
            }, CancellationToken.None);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-paused", "--storage", memoryPath, "--run", "run-reconstruct-paused-001", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        using var document = JsonDocument.Parse(output.ToString());
        var rootElement = document.RootElement;
        Assert.Equal(MetaRunReplayExecutionSources.HistoryPlusCheckpoint, rootElement.GetProperty("source").GetString());
        Assert.Equal("ask_user", rootElement.GetProperty("checkpoint").GetProperty("pendingStepId").GetString());
        Assert.True(rootElement.GetProperty("checkpoint").GetProperty("promptPresent").GetBoolean());
        Assert.Equal("finalize", rootElement.GetProperty("checkpoint").GetProperty("blockedStepIds")[0].GetString());
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

- [x] **Step 2: Run the reconstruct JSON slice to verify the new tests fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Reconstruct_"`
Expected: FAIL because `SkillCommands.RunAsync` does not route `reconstruct` and no replay result builder exists yet.

- [x] **Step 3: Add reconstruct routing and the replay result builder**

```csharp
private static async Task<int> ListMetaRunsAsync(string[] args)
{
    if (args.Length > 0 && string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
        return await PreviewMetaRunReplayAsync(args.Skip(1).ToArray());
    if (args.Length > 0 && string.Equals(args[0], "reconstruct", StringComparison.OrdinalIgnoreCase))
        return await ReconstructMetaRunReplayAsync(args.Skip(1).ToArray());

    // existing code...
}

private static async Task<int> ReconstructMetaRunReplayAsync(string[] args)
{
    var asJson = args.Contains("--json");
    var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
    if (string.IsNullOrWhiteSpace(sessionId))
    {
        Console.Error.WriteLine("Usage: openclaw skills meta-runs reconstruct <session-id> --run <run-id> [--storage <path>] [--json]");
        return 2;
    }

    var requestedRunId = GetOptionValue(args, "--run");
    if (string.IsNullOrWhiteSpace(requestedRunId))
    {
        Console.Error.WriteLine("--run <run-id> is required for meta-runs reconstruct.");
        return 2;
    }

    var storagePath = GetOptionValue(args, "--storage");
    var store = OpenMemoryStore(storagePath);
    try
    {
        var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
        if (session is null)
        {
            Console.Error.WriteLine($"Session '{sessionId}' not found.");
            return 1;
        }

        var run = session.MetaRunHistory.FirstOrDefault(run => string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal));
        if (run is null)
        {
            Console.Error.WriteLine($"Run '{requestedRunId}' not found in session '{sessionId}'.");
            return 1;
        }

        var replay = BuildReplayResult(sessionId, run, session.MetaExecutionCheckpoint);
        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(replay, CoreJsonContext.Default.MetaRunReplayResultResponse));
        }
        else
        {
            WriteReplayResultText(replay);
        }

        return 0;
    }
    finally
    {
        switch (store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
```

```csharp
private static MetaRunReplayResultResponse BuildReplayResult(
    string sessionId,
    SessionMetaRunRecord run,
    SessionMetaExecutionCheckpoint? checkpoint)
{
    var checkpointSummary = TryBuildReplayCheckpointSummary(run, checkpoint, out var source)
        ? source.summary
        : null;

    return new MetaRunReplayResultResponse
    {
        SessionId = sessionId,
        RunId = run.RunId,
        SkillName = run.SkillName,
        Mode = MetaRunReplayExecutionModes.AuditReconstruction,
        Status = run.Status,
        Source = checkpointSummary is null
            ? MetaRunReplayExecutionSources.HistoryOnly
            : MetaRunReplayExecutionSources.HistoryPlusCheckpoint,
        FinalText = run.FinalText,
        Error = run.Error,
        ErrorCode = run.ErrorCode,
        Timeline = run.StepResults.Select(static (step, index) => new MetaRunReplayTimelineItem
        {
            Sequence = index + 1,
            StepId = step.Id,
            Kind = step.Kind,
            Status = step.Status,
            FailureCode = step.FailureCode,
            DurationMs = step.DurationMs,
            Continued = step.Continued,
            Source = MetaRunReplayTimelineSources.RunHistory
        }).ToArray(),
        Checkpoint = checkpointSummary,
        ProposalSummary = new MetaRunProposalSummary()
    };
}
```

- [x] **Step 4: Run the reconstruct JSON slice to verify it passes**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Reconstruct_"`
Expected: PASS for the reconstruct JSON tests.

- [x] **Step 5: Record slice completion**

Suggested commit message if this slice is later committed separately: `feat: add meta run reconstruct json surface`

## Task 3: Add Reconstruct Text Output, Help, And Doc Sync

**Files:**

- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
- Modify: `src/OpenClaw.Tests/CliProgramTests.cs`
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

- [x] **Step 1: Write the failing reconstruct text/help tests**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Reconstruct_Text_PrintsTimelineAndCheckpointSections()
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
                Id = "sess-meta-reconstruct-text",
                ChannelId = "cli",
                SenderId = "tester",
                MetaRunHistory =
                {
                    new SessionMetaRunRecord
                    {
                        RunId = "run-reconstruct-text-001",
                        SkillName = "meta-flow",
                        Status = "paused",
                        StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:10:00Z"),
                        CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:10:03Z"),
                        StepResults =
                        {
                            new SessionMetaStepResult
                            {
                                Id = "draft",
                                Kind = "llm_chat",
                                Status = "completed",
                                DurationMs = 4
                            }
                        }
                    }
                },
                MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                {
                    SkillName = "meta-flow",
                    PendingStepId = "ask_user",
                    Prompt = "Need more detail",
                    PendingStepIds = ["ask_user"],
                    BlockedStepIds = ["finalize"]
                }
            }, CancellationToken.None);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-text", "--storage", memoryPath, "--run", "run-reconstruct-text-001"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Replay reconstruction for run: run-reconstruct-text-001", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Mode: {MetaRunReplayExecutionModes.AuditReconstruction}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Source: {MetaRunReplayExecutionSources.HistoryPlusCheckpoint}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Timeline:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("- 1 | step=draft | kind=llm_chat | status=completed | duration_ms=4", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Checkpoint:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Pending step: ask_user", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Replay available:", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Missing replay inputs:", output.ToString(), StringComparison.Ordinal);
    }
    finally
    {
        Console.SetOut(previousOut);
        Console.SetError(previousError);
        Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public async Task Main_Help_ListsSkillsMetaRunsReconstructCommand()
{
    var previousOut = Console.Out;
    using var output = new StringWriter();
    try
    {
        Console.SetOut(output);

        var exitCode = await OpenClaw.Cli.Program.Main(["--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("openclaw skills meta-runs reconstruct <session-id> --run <run-id> [--storage <path>] [--json]", output.ToString(), StringComparison.Ordinal);
    }
    finally
    {
        Console.SetOut(previousOut);
    }
}
```

- [x] **Step 2: Run the failing text/help slice**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Reconstruct_Text|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsReconstructCommand"`
Expected: FAIL because reconstruct text formatting and help text are not implemented yet.

- [x] **Step 3: Implement text formatters, help updates, and migration-doc wording**

```csharp
private static void WriteReplayResultText(MetaRunReplayResultResponse replay)
{
    Console.WriteLine($"Replay reconstruction for run: {replay.RunId}");
    Console.WriteLine($"Session: {replay.SessionId}");
    Console.WriteLine($"Skill: {replay.SkillName}");
    Console.WriteLine($"Mode: {replay.Mode}");
    Console.WriteLine($"Source: {replay.Source}");
    Console.WriteLine($"Status: {replay.Status}");
    if (!string.IsNullOrWhiteSpace(replay.FinalText))
        Console.WriteLine($"Final text: {replay.FinalText}");
    if (!string.IsNullOrWhiteSpace(replay.ErrorCode))
        Console.WriteLine($"Error code: {replay.ErrorCode}");
    if (!string.IsNullOrWhiteSpace(replay.Error))
        Console.WriteLine($"Error: {replay.Error}");

    Console.WriteLine("Timeline:");
    foreach (var item in replay.Timeline)
        Console.WriteLine(FormatReplayTimelineItem(item));

    if (replay.Checkpoint is not null)
    {
        Console.WriteLine("Checkpoint:");
        Console.WriteLine($"Pending step: {replay.Checkpoint.PendingStepId}");
        if (replay.Checkpoint.BlockedStepIds.Length > 0)
            Console.WriteLine($"Blocked steps: {string.Join(", ", replay.Checkpoint.BlockedStepIds)}");
    }

    Console.WriteLine("Proposal summary:");
    Console.WriteLine($"Available: {(replay.ProposalSummary.Available ? "yes" : "no")}");
    Console.WriteLine($"Reason: {replay.ProposalSummary.Reason}");
}
```

```text
openclaw skills meta-runs replay <session-id> --run <run-id> [--storage <path>] [--json]
openclaw skills meta-runs reconstruct <session-id> --run <run-id> [--storage <path>] [--json]
```

```markdown
1. **P1: Meta run history 的 replay 与运维能力。** 当前已区分 preview-only replay availability check 与可执行的 audit reconstruction surface；前者只报告证据是否足够，后者会从 persisted history 和可选 checkpoint 生成独立的 replay result contract，但仍不重新执行工具或模型。
```

- [x] **Step 4: Run the wider meta-runs regression slice**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsCommand"`
Expected: PASS, including existing replay preview coverage and the new reconstruct/help tests.

- [x] **Step 5: Run compile safety net and record slice completion**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`
Expected: PASS

Suggested commit message if this slice is later committed separately: `feat: add meta run audit reconstruction cli`
