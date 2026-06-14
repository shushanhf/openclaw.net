# Meta Review Workflow Object Governance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce a dedicated durable review-workflow object for meta-run proposals so governance state is tracked independently from proposal status transitions.

**Architecture:** Keep runtime orchestration unchanged and extend only governance/product surfaces. Reuse existing `ILearningProposalStore` for durability by adding a dedicated workflow kind keyed 1:1 to each meta-run proposal, append transition history metadata on every review mutation, and expose additive `workflow` sections in show/mutation JSON outputs. Preserve all existing lifecycle semantics and error contracts.

**Tech Stack:** .NET 10, C#, xUnit, System.Text.Json source generation (`CoreJsonContext`), existing `SkillCommands` proposal mutation pipeline

---

## Scope Check

This request is a single subsystem: governance review workflow object for `skills meta-runs proposals`. No split into multiple plans is required.

## Worktree Requirement

Use a dedicated worktree before implementation.

```bash
git worktree add ../openclaw-review-workflow metaskill
```

Then implement in the new worktree.

## File Structure

- Create: `src/OpenClaw.Core/Models/MetaRunReviewWorkflowModels.cs`
  Responsibility: define workflow constants, workflow action/stage values, and metadata key schema for durable workflow records.
- Modify: `src/OpenClaw.Core/Models/LearningModels.cs`
  Responsibility: add a dedicated durable kind for workflow objects (`meta_run_review_workflow`).
- Modify: `src/OpenClaw.Core/Models/Session.cs`
  Responsibility: add additive response DTOs (`MetaRunProposalWorkflowDetail`) and source-gen coverage in `CoreJsonContext`.
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
  Responsibility: create/update workflow durable records during `accept|dismiss|rollback|change`; hydrate workflow sections in mutation/show responses.
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
  Responsibility: add E2E tests for workflow object creation, transition history append, and non-drift on denied/conflict flows.
- Modify: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`
  Responsibility: add governance-focused contract assertions for machine-readable workflow payload sections.
- Modify: `docs/opensquilla-meta-skill-migration.md`
  Responsibility: mark review-workflow-object gap closed and add evidence links.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
  Responsibility: mirror Chinese migration conclusion and evidence.

---

### Task 1: Add Workflow Object Contracts In Core Models

**Files:**
- Create: `src/OpenClaw.Core/Models/MetaRunReviewWorkflowModels.cs`
- Modify: `src/OpenClaw.Core/Models/LearningModels.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`

- [ ] **Step 1: Write failing contract test for workflow payload shape in mutation response**

Add to `SkillCommandsMetaGovernanceTests.cs`:

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_IncludesWorkflowSection()
{
    // Arrange paused run with checkpoint
    // Act accept --json
    // Assert response.workflow exists
    // Assert response.workflow.workflowId starts with "meta-run-workflow:"
    // Assert response.workflow.stage == "decision_recorded"
    // Assert response.workflow.lastAction == "accept"
}
```

- [ ] **Step 2: Run test to verify failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_Json_IncludesWorkflowSection"
```

Expected: FAIL because no workflow section exists in mutation response yet.

- [ ] **Step 3: Add workflow model constants and DTOs**

Create `MetaRunReviewWorkflowModels.cs`:

```csharp
namespace OpenClaw.Core.Models;

public static class MetaRunReviewWorkflowKinds
{
    public const string DurableKind = "meta_run_review_workflow";
}

public static class MetaRunReviewWorkflowStages
{
    public const string DecisionRecorded = "decision_recorded";
    public const string RolledBack = "rolled_back";
}

public static class MetaRunReviewWorkflowActions
{
    public const string Accept = "accept";
    public const string Dismiss = "dismiss";
    public const string Rollback = "rollback";
    public const string Change = "change";
}

public static class MetaRunReviewWorkflowMetadata
{
    public const string SessionId = "meta_run_workflow_session_id";
    public const string ProposalId = "meta_run_workflow_proposal_id";
    public const string WorkflowId = "meta_run_workflow_id";
    public const string Stage = "meta_run_workflow_stage";
    public const string LastAction = "meta_run_workflow_last_action";
    public const string LastActorId = "meta_run_workflow_last_actor_id";
    public const string LastChangedAtUtc = "meta_run_workflow_last_changed_at_utc";
    public const string TransitionCount = "meta_run_workflow_transition_count";
}
```

In `LearningModels.cs`, add kind constant:

```csharp
public static class LearningProposalKind
{
    public const string MetaRunProposal = "meta_run_proposal";
    public const string MetaRunReviewWorkflow = "meta_run_review_workflow";
}
```

In `Session.cs`, add additive DTO:

```csharp
public sealed class MetaRunProposalWorkflowDetail
{
    public required string WorkflowId { get; init; }
    public required string Stage { get; init; }
    public required string LastAction { get; init; }
    public string? LastActorId { get; init; }
    public DateTimeOffset? LastChangedAtUtc { get; init; }
    public int TransitionCount { get; init; }
}
```

And source-gen entries in `CoreJsonContext`:

```csharp
[JsonSerializable(typeof(MetaRunProposalWorkflowDetail))]
```

- [ ] **Step 4: Run compile validation**

Run:

```bash
dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal
```

Expected: PASS build, tests still failing on behavior implementation.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Core/Models/MetaRunReviewWorkflowModels.cs src/OpenClaw.Core/Models/LearningModels.cs src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs
git commit -m "feat(governance): add meta-run review workflow object contracts"
```

---

### Task 2: Persist Workflow Object During Proposal Mutations

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Write failing E2E test for workflow durable object lifecycle**

Add to `SkillCommandsTests.cs`:

```csharp
[Fact]
public async Task RunAsync_Phase3_E2E_AcceptRollbackChange_PersistsWorkflowObjectHistory()
{
    // Arrange paused run, accept -> rollback -> change --to accept
    // Assert durable workflow proposal exists with kind meta_run_review_workflow
    // Assert transition_count == 3
    // Assert last_action == change
    // Assert stage == decision_recorded
}
```

- [ ] **Step 2: Run test to verify failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_Phase3_E2E_AcceptRollbackChange_PersistsWorkflowObjectHistory"
```

Expected: FAIL because no workflow durable object is written.

- [ ] **Step 3: Implement workflow upsert helpers in SkillCommands**

Add helpers:

```csharp
private static string BuildMetaRunWorkflowDurableId(string sessionId, string proposalId)
    => $"meta-run-workflow:{sessionId}:{proposalId}";

private static void AppendMetaRunWorkflowTransitionMetadata(
    IDictionary<string, string> metadata,
    string action,
    string stage,
    DateTimeOffset changedAtUtc,
    string? actorId,
    string? reason)
{
    var count = 0;
    if (metadata.TryGetValue(MetaRunReviewWorkflowMetadata.TransitionCount, out var raw)
        && int.TryParse(raw, out var parsed))
        count = parsed;

    metadata[$"meta_run_workflow_transition_{count:D4}_action"] = action;
    metadata[$"meta_run_workflow_transition_{count:D4}_stage"] = stage;
    metadata[$"meta_run_workflow_transition_{count:D4}_changed_at_utc"] = changedAtUtc.ToString("O", CultureInfo.InvariantCulture);
    metadata[$"meta_run_workflow_transition_{count:D4}_actor_id"] = actorId ?? string.Empty;
    metadata[$"meta_run_workflow_transition_{count:D4}_reason"] = reason ?? string.Empty;
    metadata[MetaRunReviewWorkflowMetadata.TransitionCount] = (count + 1).ToString(CultureInfo.InvariantCulture);
}
```

Upsert on every successful mutation (`accept|dismiss|rollback|change`) after durable proposal save:

```csharp
await UpsertMetaRunReviewWorkflowAsync(
    learningProposalStore,
    sessionId,
    proposalId,
    action,
    lifecycleStatus,
    reviewedAtUtc,
    operatorId,
    reason,
    CancellationToken.None);
```

- [ ] **Step 4: Run targeted tests**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_Phase3_E2E_AcceptRollbackChange_PersistsWorkflowObjectHistory|FullyQualifiedName~RunAsync_MetaRuns_Proposals_"
```

Expected: PASS for new test and no regression in existing proposals lifecycle suite.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat(governance): persist dedicated review workflow object for proposal mutations"
```

---

### Task 3: Expose Workflow Section In Mutation/Show Contracts

**Files:**
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Write failing tests for additive workflow contract in show and mutation**

Add tests:

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Show_Json_AfterAccept_IncludesWorkflowSection() { }

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Change_Json_AfterRollback_IncludesWorkflowSection() { }
```

Assertions:
- `workflow.workflowId`
- `workflow.stage`
- `workflow.lastAction`
- `workflow.transitionCount`

- [ ] **Step 2: Run tests to verify failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~IncludesWorkflowSection"
```

Expected: FAIL because response DTO does not include workflow field.

- [ ] **Step 3: Add additive fields to response DTOs and serialization**

In `Session.cs`, extend response models:

```csharp
public sealed class MetaRunProposalReviewMutationResponse
{
    // existing fields
    public MetaRunProposalWorkflowDetail? Workflow { get; init; }
}

public sealed class MetaRunDerivedProposalDetail
{
    // existing fields
    public MetaRunProposalWorkflowDetail? Workflow { get; init; }
}
```

In `SkillCommands.cs`, hydrate workflow detail from durable workflow record:

```csharp
var workflow = await GetMetaRunReviewWorkflowDetailAsync(learningProposalStore, sessionId, proposalId, CancellationToken.None);
response = response with { Workflow = workflow };
```

(If object initializer style is used in file, assign `Workflow = workflow` directly in initializer.)

- [ ] **Step 4: Run governance + lifecycle slices**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_"
```

Expected: PASS; additive response fields do not break existing JSON/text tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat(governance): expose review workflow detail in proposal contracts"
```

---

### Task 4: Add Non-Drift Guards For Denied/Conflict Paths

**Files:**
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`

- [ ] **Step 1: Write failing tests for workflow non-drift under denied/conflict**

Add tests:

```csharp
[Fact]
public async Task RunAsync_Phase3_E2E_ChangeDenied_DoesNotAdvanceWorkflowTransitionCount() { }

[Fact]
public async Task RunAsync_Phase3_E2E_ConflictAcceptAfterDismiss_DoesNotAdvanceWorkflowTransitionCount() { }
```

Expected behavior:
- permission denied and proposal_already_reviewed must not append workflow transition entries.

- [ ] **Step 2: Run tests to verify failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~DoesNotAdvanceWorkflowTransitionCount"
```

Expected: FAIL if denied/conflict paths accidentally write workflow transitions.

- [ ] **Step 3: Implement strict write points**

In `SkillCommands.cs`, ensure `UpsertMetaRunReviewWorkflowAsync(...)` is called only after successful durable mutation branches, never in:
- permission_denied returns
- proposal_not_found/session_not_found returns
- invalid_lifecycle_transition returns
- proposal_already_reviewed conflict returns

- [ ] **Step 4: Run full governance regression set**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_|FullyQualifiedName~SkillCommandsGlobalMetaRunsTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "test(governance): guard review workflow object from denied and conflict drift"
```

---

### Task 5: Update Migration Documentation

**Files:**
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

- [ ] **Step 1: Update acceptance matrix row for governance review workflow object**

Set row “Review workflow object (governance)” from Partial to Complete, with evidence references to:
- workflow object persistence in `SkillCommands.cs`
- workflow response payload contracts in `Session.cs`
- E2E non-drift tests in `SkillCommandsTests.cs`

- [ ] **Step 2: Update strict migration conclusion wording**

Add one bullet in both docs:
- dedicated review workflow object now durable and independently auditable.

- [ ] **Step 3: Run docs-linked regression command and record result**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_"
```

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md
git commit -m "docs(governance): close review workflow object migration gap"
```

---

## Self-Review

### 1) Spec coverage

- Requirement: continue closing governance-layer review workflow object.
  - Covered by Tasks 1-4: object contract, durable persistence, response exposure, and non-drift guards.
- Requirement: keep existing lifecycle semantics stable.
  - Covered by Tasks 2-4 via targeted + full proposal regression slices.
- Requirement: update migration docs to reflect closure.
  - Covered by Task 5.

No uncovered requirement remains.

### 2) Placeholder scan

- No TODO/TBD placeholders.
- Every task includes explicit files, commands, and expected outcomes.
- Code-modifying steps provide concrete snippets.

### 3) Type consistency

- Workflow naming is consistent:
  - `MetaRunReviewWorkflowKinds.DurableKind`
  - `MetaRunProposalWorkflowDetail`
  - `BuildMetaRunWorkflowDurableId`
  - `UpsertMetaRunReviewWorkflowAsync`
- Metadata keys are from `MetaRunReviewWorkflowMetadata.*` namespace.

No type/name mismatch found.
