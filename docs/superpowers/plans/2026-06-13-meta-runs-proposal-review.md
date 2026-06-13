# Meta-Runs Proposal Review Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add operator-facing `accept`/`dismiss` review actions for derived `meta-runs proposals` and surface review state in list/show output without triggering execution side effects.

**Architecture:** Keep derived proposal generation read-only, add an independent session-scoped review record store, and merge review state into existing list/show responses as additive fields. Implement new CLI subcommands (`accept`, `dismiss`) with idempotent same-action success and opposite-action conflict rejection.

**Tech Stack:** .NET 10, xUnit, `System.Text.Json` source generation (`CoreJsonContext`), existing OpenClaw CLI storage resolution rules

---

## Scope And Constraints

- Keep this slice non-executing: no replay/resume/tool/model execution on review actions.
- Keep existing derived proposal fields backward-compatible; review fields are additive.
- Use independent review persistence; do not write decision state into `Session.MetaRunHistory`.
- Do not integrate `LearningProposal` lifecycle in this slice.

## File Structure

### Create

- `src/OpenClaw.Cli/MetaRunProposalReviewStore.cs`
  - Session-scoped file persistence for proposal review records.
  - Read/write by `sessionId` + `proposalId`.

### Modify

- `src/OpenClaw.Core/Models/Session.cs`
  - Add review DTOs and constants.
  - Extend derived proposal summary/detail with additive review fields.
  - Add `[JsonSerializable]` coverage for new DTOs.
- `src/OpenClaw.Cli/SkillCommands.cs`
  - Route `meta-runs proposals accept` and `dismiss`.
  - Merge review state into list/show output.
  - Add mutation response output (json/text) and conflict/idempotency handling.
  - Extend help text.
- `src/OpenClaw.Tests/SkillCommandsTests.cs`
  - Add TDD coverage for success/idempotency/conflict/not-found/output contracts.
- `src/OpenClaw.Tests/CliProgramTests.cs`
  - Add help text coverage for new commands.
- `docs/opensquilla-meta-skill-migration.md`
  - Document derived review-state surface and non-executing semantics.
- `docs/zh-CN/opensquilla-meta-skill-migration.md`
  - Chinese mirror update.

## Task 1: Define CLI Contracts With Failing Tests

**Files:**
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
- Modify: `src/OpenClaw.Tests/CliProgramTests.cs`

- [ ] **Step 1: Add failing test for accept success (JSON)**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_PrintsAppliedReview()
{
    // Arrange paused proposal candidate in session history.
    // Act: meta-runs proposals accept <session> --proposal <id> --json
    // Assert: exit 0, stderr empty, json.reviewStatus == "accepted", json.alreadyReviewed == false.
}
```

- [ ] **Step 2: Add failing test for dismiss success with optional reason (JSON)**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Dismiss_Json_WithReason_PrintsAppliedReview()
{
    // Act: ... proposals dismiss ... --reason "operator reviewed" --json
    // Assert reason round-trips and status == dismissed.
}
```

- [ ] **Step 3: Add failing tests for idempotency and opposite-action conflict**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_SecondCall_IsIdempotentSuccess() { }

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Dismiss_AfterAccept_ReturnsConflict() { }
```

- [ ] **Step 4: Add failing tests for list/show review visibility and JSON error-path empty stdout**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Json_IncludesReviewStatus() { }

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Show_Json_IncludesReviewSection() { }

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_Conflict_WritesNoPartialJson() { }
```

- [ ] **Step 5: Add failing help test for new commands**

```csharp
[Fact]
public async Task Main_Help_ListsSkillsMetaRunsProposalReviewCommands()
{
    // Assert help contains accept + dismiss command lines.
}
```

- [ ] **Step 6: Run targeted test slice and confirm failures**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Accept|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Dismiss|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsProposalReviewCommands"`

Expected: FAIL due to missing command routing / models / store.

- [ ] **Step 7: Commit failing-contract tests**

```bash
git add src/OpenClaw.Tests/SkillCommandsTests.cs src/OpenClaw.Tests/CliProgramTests.cs
git commit -m "test: add failing contracts for meta-run proposal review commands"
```

## Task 2: Add Review DTOs And Source-Generated JSON Coverage

**Files:**
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Add review status/decision constants and DTOs**

```csharp
public static class MetaRunProposalReviewStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Dismissed = "dismissed";
}

public sealed class MetaRunProposalReviewRecord
{
    public required string SessionId { get; init; }
    public required string ProposalId { get; init; }
    public required string ReviewStatus { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset ReviewedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? ReviewedBy { get; init; }
}

public sealed class MetaRunProposalReviewMutationResponse
{
    public required string SessionId { get; init; }
    public required string ProposalId { get; init; }
    public required string ReviewStatus { get; init; }
    public bool AlreadyReviewed { get; init; }
    public DateTimeOffset ReviewedAtUtc { get; init; }
    public string? Reason { get; init; }
}

public sealed class MetaRunProposalReviewSummary
{
    public string ReviewStatus { get; init; } = MetaRunProposalReviewStatuses.Pending;
    public DateTimeOffset? ReviewedAtUtc { get; init; }
}

public sealed class MetaRunProposalReviewDetail
{
    public required string Status { get; init; }
    public required DateTimeOffset ReviewedAtUtc { get; init; }
    public string? Reason { get; init; }
}
```

- [ ] **Step 2: Extend proposal summary/detail with additive review fields**

```csharp
// MetaRunDerivedProposalSummary
public string ReviewStatus { get; init; } = MetaRunProposalReviewStatuses.Pending;
public DateTimeOffset? ReviewedAtUtc { get; init; }

// MetaRunDerivedProposalDetail
public MetaRunProposalReviewDetail? Review { get; init; }
```

- [ ] **Step 3: Add `CoreJsonContext` annotations for all new DTOs**

```csharp
[JsonSerializable(typeof(MetaRunProposalReviewRecord))]
[JsonSerializable(typeof(MetaRunProposalReviewRecord[]))]
[JsonSerializable(typeof(MetaRunProposalReviewMutationResponse))]
[JsonSerializable(typeof(MetaRunProposalReviewDetail))]
```

- [ ] **Step 4: Run compile-focused test to verify AOT source-gen coverage**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`

Expected: PASS compile; tests still fail on missing CLI implementation.

- [ ] **Step 5: Commit model contract changes**

```bash
git add src/OpenClaw.Core/Models/Session.cs
git commit -m "feat: add meta-run proposal review DTO contracts"
```

## Task 3: Implement Independent Review Store

**Files:**
- Create: `src/OpenClaw.Cli/MetaRunProposalReviewStore.cs`
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Add failing store-focused tests through CLI behavior (persist across repeated calls)**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_ThenShow_ReflectsPersistedReview() { }
```

- [ ] **Step 2: Implement session-scoped file persistence helper**

```csharp
internal sealed class MetaRunProposalReviewStore
{
    public MetaRunProposalReviewStore(string memoryPath) { /* use review folder under memory root */ }

    public ValueTask<IReadOnlyDictionary<string, MetaRunProposalReviewRecord>> LoadBySessionAsync(string sessionId, CancellationToken ct);

    public ValueTask<MetaRunProposalReviewRecord?> GetAsync(string sessionId, string proposalId, CancellationToken ct);

    public ValueTask SaveAsync(MetaRunProposalReviewRecord record, CancellationToken ct);
}
```

- [ ] **Step 3: Ensure deterministic file location and safe key encoding**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Accept_ThenShow_ReflectsPersistedReview"`

Expected: still failing until CLI wiring exists; store class compiles.

- [ ] **Step 4: Commit store foundation**

```bash
git add src/OpenClaw.Cli/MetaRunProposalReviewStore.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat: add session-scoped meta-run proposal review store"
```

## Task 4: Wire CLI Accept/Dismiss And Merge Review Into List/Show

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Route subcommands under `meta-runs proposals`**

```csharp
if (args.Length > 0 && string.Equals(args[0], "accept", StringComparison.OrdinalIgnoreCase))
    return AcceptMetaRunProposalAsync(args.Skip(1).ToArray());
if (args.Length > 0 && string.Equals(args[0], "dismiss", StringComparison.OrdinalIgnoreCase))
    return DismissMetaRunProposalAsync(args.Skip(1).ToArray());
```

- [ ] **Step 2: Implement shared review mutation flow**

```csharp
private static async Task<int> ReviewMetaRunProposalAsync(string[] args, string targetStatus, bool allowReason)
{
    // parse session/proposal/storage/json/reason
    // resolve session + derived proposal existence
    // load review record and apply: idempotent same-status success, opposite-status conflict
    // persist first decision
    // emit text/json response
}
```

- [ ] **Step 3: Merge review state into list output**

```csharp
private static MetaRunDerivedProposalSummary[] ApplyReviewSummary(
    string sessionId,
    MetaRunDerivedProposalSummary[] proposals,
    IReadOnlyDictionary<string, MetaRunProposalReviewRecord> reviews)
{
    // add reviewStatus/reviewedAtUtc when present
}
```

- [ ] **Step 4: Merge review state into show detail output**

```csharp
private static MetaRunDerivedProposalDetail ApplyReviewDetail(
    MetaRunDerivedProposalDetail detail,
    MetaRunProposalReviewRecord? review)
{
    // populate detail.Review when review exists
}
```

- [ ] **Step 5: Enforce JSON failure-path contract**

- Failure branches in accept/dismiss must return non-zero and write only stderr.
- No partial JSON on stdout for conflict/not-found/usage errors.

- [ ] **Step 6: Run proposals-focused regression**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_"`

Expected: PASS for existing and new proposals list/show/review tests.

- [ ] **Step 7: Commit CLI behavior implementation**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat: add meta-run proposal accept dismiss workflow"
```

## Task 5: Help Text And Migration Documentation

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Tests/CliProgramTests.cs`
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

- [ ] **Step 1: Add help lines for new commands**

```text
openclaw skills meta-runs proposals accept <session-id> --proposal <id> [--storage <path>] [--json]
openclaw skills meta-runs proposals dismiss <session-id> --proposal <id> [--reason <text>] [--storage <path>] [--json]
```

- [ ] **Step 2: Update help tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsProposalsCommands|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsProposalReviewCommands"`

Expected: PASS

- [ ] **Step 3: Update EN/ZH migration docs with review-state semantics**

Required points:
- derived proposals remain read-only evidence views
- accept/dismiss are operator review decisions only
- same-action idempotent success, opposite-action conflict rejection
- list/show now expose review state additively

- [ ] **Step 4: Run wider meta-runs regression slice**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"`

Expected: PASS with replay/reconstruct/proposals and help contracts intact.

- [ ] **Step 5: Commit docs + help updates**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/CliProgramTests.cs docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md
git commit -m "docs: document meta-run proposal review workflow"
```

## Final Verification Checklist

- [ ] `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal`
- [ ] `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_"`
- [ ] `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"`
- [ ] Confirm no removals of existing `proposals`/`proposals show` JSON fields
- [ ] Confirm AOT source-gen annotations exist for every new review DTO
