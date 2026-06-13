# OpenSquilla Meta-Run Audit Replay Design

Date: 2026-06-13

## Summary

Implement the next P1 operator-facing slice for OpenSquilla-style meta runs by adding an executable audit replay surface on top of persisted OpenClaw session state.

Phase 1 does not attempt true re-execution of the original meta run. Instead, it reconstructs an auditable replay result from persisted `MetaRunHistory` and, when still available, the matching `MetaExecutionCheckpoint`. This lifts the current preview-only replay availability check into a real operator workflow without introducing new model/tool side effects or requiring full persistence of original step inputs.

The same design cycle also reserves interface space for future proposal workflows, but does not implement proposal acceptance or any other mutating operator action in this phase.

## Goals

### P1: Audit Replay Reconstruction

Add a real CLI workflow that reconstructs a persisted meta run into a replay result object that operators can inspect in both text and JSON forms.

The replay result must:

- be derived from persisted session state only
- distinguish whether it was built from run history alone or from run history plus an in-memory-compatible persisted checkpoint snapshot
- preserve final status, final text, error, and per-step execution evidence
- expose a stable machine-readable contract separate from the existing preview contract
- remain safe for NativeAOT and source-generated JSON serialization

### P1: Proposal Interface Reservation

Define the data-model and CLI integration points needed for future proposal workflows so that Phase 1 replay output does not need a breaking redesign later.

Phase 1 only needs:

- a stable placeholder proposal summary on replay results
- documented CLI surface reservation for proposal listing/show flows

### Phase 2: Derived Proposal View

Add a read-only proposal inspection surface that derives candidate proposal summaries from persisted meta-run evidence.

Phase 2 proposal output must:

- use only `Session.MetaRunHistory`, optional `Session.MetaExecutionCheckpoint`, and replay-visible audit evidence
- identify itself as a derived view rather than a persisted proposal entity
- keep `list` and `show` read-only
- expose a minimal stable JSON field set that can survive a later migration to a store-backed proposal model

For the currently shipped `show` detail surface, the derived operator view may also expand persisted evidence in two bounded ways without changing that read-only boundary:

- `evidence`
  - run-level persisted evidence summary only
  - may group timeline step IDs, error code, error text, and final text into one additive read-only sub-object

- `steps[]`
  - step-level persisted evidence only
  - includes step identity, kind, status, failure code when present, duration, and continuation metadata
- `checkpoint`
  - optional structured paused-run summary
  - includes `pendingStepId`, pending/blocked step sets, `promptPresent`, output step IDs, and failure-alias step IDs

These additions are operator-facing evidence summaries, not durable proposal lifecycle state.

### Phase 3: LearningProposal-Backed Migration

Treat the derived proposal view as an operator bridge, not the final proposal architecture.

The long-term target is to back `meta-runs proposals` with persisted `LearningProposal` entities once proposal lifecycle, provenance, or cross-surface reuse becomes necessary.

## Non-Goals

This design does not include:

- true re-execution of meta steps
- re-calling tools or models during replay
- persistence of full original prompt context, step inputs, or tool arguments into `SessionMetaRunRecord`
- proposal acceptance, proposal mutation, or proposal-triggered execution
- `LearningProposal` store integration in the current replay/proposal slice
- replay result write-back as a new meta run
- parallel scheduling changes
- `skill_exec` stdin support

## Semantic Boundary

Phase 1 replay means audit reconstruction, not re-execution.

That boundary is mandatory:

- replay uses only persisted evidence already present in the session store
- replay cannot trigger new tool side effects
- replay cannot claim equivalence to the original runtime control flow when required inputs were never retained
- replay is an operator artifact, not a second execution attempt

This preserves the meaning of the current preview contract: preview answers whether persisted evidence is sufficient to construct a replay artifact. The new reconstruct command consumes that evidence and produces the artifact itself.

## Existing Surfaces To Preserve

The implementation must preserve current behavior in these files:

- `src/OpenClaw.Cli/SkillCommands.cs`
  - existing `meta-runs` listing
  - existing preview-only `meta-runs replay`
- `src/OpenClaw.Core/Models/Session.cs`
  - `SessionMetaRunRecord` as the minimal persisted run summary
  - existing preview DTOs and stable preview constants
- `src/OpenClaw.Agent/AgentRuntime.cs`
  - current `AppendMetaRunHistory(...)` writer path
  - current `SaveMetaExecutionCheckpoint(...)` and checkpoint restoration semantics

Phase 1 replay should layer on top of those contracts rather than redefine them.

## Architecture

### Data Sources

Replay reconstruction uses two evidence sources:

1. `Session.MetaRunHistory`
   - authoritative run identity source
   - always required
2. `Session.MetaExecutionCheckpoint`
   - optional augmentation source
   - only used when it belongs to the same skill/run context and can safely enrich a paused replay view

The checkpoint is not an independently addressable replay object. It only augments a run selected from history.

### CLI Surface

Keep the current preview contract intact:

- `openclaw skills meta-runs replay <session-id> --run <run-id> [--storage <path>] [--json]`
  - remains preview-only

Add a new executable audit replay command:

- `openclaw skills meta-runs reconstruct <session-id> --run <run-id> [--storage <path>] [--json]`
  - builds and prints an audit replay result

Phase 2 now ships the following read-only proposal surface:

- `openclaw skills meta-runs proposals <session-id> [--run <run-id>] [--json]`
- `openclaw skills meta-runs proposals show <session-id> --proposal <id> [--json]`

This surface returns derived proposal summaries/details sourced from meta-run audit evidence rather than persisted `LearningProposal` entities.

`meta-runs proposals show` may expose richer read-only evidence than the list surface, including step-level trace summaries and structured checkpoint metadata for paused runs.

The shipped detail surface may also group run-level evidence into an additive `evidence` object while preserving the earlier top-level fields as legacy-compatible mirrors.

Operator guidance: prefer `checkpoint` and `evidence` as the canonical grouped detail surfaces. Top-level `PendingStepId`, `PendingStepIds`, `BlockedStepIds`, `TimelineStepIds`, `ErrorCode`, `Error`, and `FinalText` remain compatibility mirrors for existing consumers.

Phase 1 and Phase 2 should not advertise proposal `accept` because no accepted-execution semantics exist yet.

## Data Model Design

Do not expand `SessionMetaRunRecord` into a full execution input archive in this phase.

Instead, add replay-specific DTOs alongside the existing preview DTOs in `src/OpenClaw.Core/Models/Session.cs`.

### `MetaRunReplayResultResponse`

Top-level replay reconstruction result.

Fields:

- `SessionId`
- `RunId`
- `SkillName`
- `Mode`
  - fixed contract value such as `audit_reconstruction`
- `Status`
  - mirrors run status: `completed`, `failed`, or `paused`
- `Source`
  - `history_only`
  - `history_plus_checkpoint`
- `FinalText`
- `Error`
- `ErrorCode`
- `Timeline`
  - ordered replay steps
- `Checkpoint`
  - optional summary object
- `ProposalSummary`
  - placeholder summary for future proposal workflows

### `MetaRunReplayTimelineItem`

Represents one reconstructed step in replay output.

Fields:

- `Sequence`
- `StepId`
- `Kind`
- `Status`
- `FailureCode`
- `DurationMs`
- `Continued`
- `Source`
  - `run_history` or `checkpoint`
- `Notes`
  - optional operator-facing annotation such as pending/blocked context

### `MetaRunReplayCheckpointSummary`

Optional augmentation object derived from `SessionMetaExecutionCheckpoint`.

Fields:

- `PendingStepId`
- `PendingStepIds`
- `BlockedStepIds`
- `PromptPresent`
  - boolean only; do not surface the raw prompt in Phase 1
- `OutputStepIds`
- `FailureAliasStepIds`

### `MetaRunProposalSummary`

Placeholder object for future proposal workflows.

Fields:

- `Available`
- `Count`
- `Kinds`
- `Reason`

Phase 1 may always populate this with an empty/unavailable summary, but the field must exist so later proposal support does not require a breaking replay contract change.

### Phase 2 Proposal DTO Direction

The next proposal slice should keep the JSON contract intentionally small and migration-friendly.

Whether list/show results use dedicated DTOs or a shared summary/detail pair, the minimum stable field set should be:

- `Id`
- `Kind`
- `Title`
- `Summary`
- `Source`
- `AvailableActions`

For the shipped detail shape, `show` may add bounded evidence fields on top of that minimum set, including:

- `RunId`
- `SkillName`
- `Status`
- `PendingStepId`
- `PendingStepIds`
- `BlockedStepIds`
- `TimelineStepIds`
- `Evidence`
- `Steps`
- `Checkpoint`
- `ErrorCode`
- `Error`
- `FinalText`

The compatibility rule is that the list/show shared summary fields above remain stable, while richer `show`-only evidence fields stay additive and read-only. When both grouped and top-level forms exist, grouped `checkpoint` / `evidence` fields are the preferred operator-facing shape and top-level duplicates are compatibility mirrors.

For Phase 2, `Source` should use a derived-evidence contract value such as `derived_meta_run_evidence` so operators can distinguish it from future store-backed proposal entities.

`AvailableActions` should remain read-only in practice, for example empty or limited to `show`, until a real proposal lifecycle exists.

### Contract Constants

Add stable constants near the existing preview constants for:

- replay modes
- replay result sources
- replay timeline sources
- proposal summary reasons

These constants should live in `Session.cs` so CLI/tests can share them without string drift.

## Reconstruction Rules

### Run Selection

Replay reconstruction starts from a single `SessionMetaRunRecord` selected by session ID and run ID.

If no session exists or the run is missing, the CLI must keep the current not-found behavior style used by other `meta-runs` commands.

### History-Only Reconstruction

For completed or failed runs, or for paused runs without a usable checkpoint, reconstruction must:

- copy final status fields from `SessionMetaRunRecord`
- build `Timeline` from `StepResults` in persisted order
- set `Source = history_only`
- omit checkpoint details

### Checkpoint-Augmented Reconstruction

For paused runs with a compatible persisted checkpoint still present on the same session, reconstruction may augment the replay result with:

- pending step ID
- pending and blocked step sets
- presence-only prompt information
- output/failure-alias key summaries
- checkpoint-derived timeline notes when useful

It must not:

- rewrite the authoritative run identity
- invent missing completed steps
- replace run-history step facts with checkpoint guesses

Checkpoint data only enriches the paused operator view.

### Timeline Construction

The timeline must be deterministic and operator-readable.

Rules:

- preserve persisted `StepResults` order as the base sequence
- assign explicit `Sequence` numbers starting at 1
- annotate paused replay context via `Notes` rather than rewriting `Status`
- use checkpoint-only summaries for pending/blocked state instead of creating synthetic completed steps

## CLI Output Design

### Proposal Output Semantics

If `meta-runs proposals` is added in the next slice, both text and JSON output must state that the returned items are derived candidate proposals, not persisted proposal records.

The CLI must not imply:

- durable proposal lifecycle state
- proposal acceptance or rejection
- rollback semantics
- stable provenance parity with a persisted proposal entity

### JSON Output

JSON output for reconstruct must serialize the new replay result DTO through `CoreJsonContext`.

It must not reuse the preview DTO or preview shape.

For proposal list/show output, the contract should preserve the minimum stable field set described above so a later `LearningProposal` migration can stay additive where possible. The current `show` detail surface may expand additive evidence fields, but it must not introduce lifecycle semantics such as acceptance, rejection, or review-state mutation.

### Text Output

Text output should stay close to the current CLI style:

- header with run/session/skill
- mode/source/status lines
- final text and error details when present
- `Timeline:` section with one line per replay step
- optional `Checkpoint:` section for paused augmentation details
- optional `Proposal summary:` section, even when empty/unavailable

Text output must not print preview-only phrases such as:

- `Replay available: yes/no`
- `Blocked by requirements:`
- `Missing replay inputs:`

Those belong to preview, not reconstruct.

## Future Migration To LearningProposal Store

Do not keep extending the derived proposal view indefinitely.

Move `meta-runs proposals` onto `ILearningProposalStore` / `LearningProposal` once any of these become required:

- stable proposal identity across runs or sessions
- complete proposal provenance details
- cross-session or cross-run proposal aggregation
- shared proposal records across CLI, dashboard, or other operator surfaces
- any mutation workflow such as accept, reject, rollback, or review state transitions

When that migration happens:

- preserve the minimum public field set from the derived-view contract where possible
- treat derived proposal IDs as compatibility shims rather than permanent entity identifiers
- prefer additive JSON expansion over replacing existing top-level fields
- update migration docs so operators know when `meta-runs proposals` stopped being evidence-derived and became store-backed

## Testing Strategy

### SkillCommands JSON Contract Tests

Add focused tests in `src/OpenClaw.Tests/SkillCommandsTests.cs` for:

- completed run reconstruct JSON from history only
- failed run reconstruct JSON with error fields retained
- paused run reconstruct JSON with checkpoint augmentation
- paused run reconstruct JSON falling back to history only when no checkpoint is present

### SkillCommands Text Contract Tests

Add focused tests in `src/OpenClaw.Tests/SkillCommandsTests.cs` for:

- stable top-level reconstruct text sections
- deterministic timeline line ordering
- checkpoint summary rendering for paused runs
- absence of preview-only wording in reconstruct output

### Help/CLI Routing Tests

Update `src/OpenClaw.Tests/CliProgramTests.cs` to cover:

- help text for `meta-runs reconstruct`
- missing `--run` argument handling
- unknown session/run handling for reconstruct

### Serialization Coverage

Update `src/OpenClaw.Core/Models/Session.cs` JSON source-generation annotations for all new replay DTOs and any placeholder proposal DTOs.

At least one test must confirm reconstruct JSON serializes through the generated context.

## Implementation Order

1. Add replay result DTOs and shared constants to `src/OpenClaw.Core/Models/Session.cs`.
2. Extend `CoreJsonContext` with the new DTOs.
3. Write failing reconstruct JSON tests in `src/OpenClaw.Tests/SkillCommandsTests.cs`.
4. Add CLI routing for `meta-runs reconstruct` in `src/OpenClaw.Cli/SkillCommands.cs`.
5. Implement `BuildReplayResult(...)` from run history plus optional checkpoint augmentation.
6. Run focused reconstruct tests until JSON output stabilizes.
7. Add failing text-output tests for reconstruct.
8. Implement reconstruct text formatting with the same anti-drift approach already used for preview formatters.
9. Update `src/OpenClaw.Tests/CliProgramTests.cs` help assertions.
10. Update migration docs to distinguish preview from audit reconstruction.

## Risks And Controls

### Risk: Replay vs Re-Execution Confusion

Control:

- keep preview and reconstruct as separate commands
- use explicit mode/source constants
- avoid words implying fresh execution in help text and docs

### Risk: Checkpoint Leakage

Control:

- expose prompt presence, not raw prompt text, in Phase 1 checkpoint summary
- expose output/failure-alias key summaries rather than all raw values by default

### Risk: Contract Drift Between Text And JSON

Control:

- define DTO constants in `Session.cs`
- add reconstruct-focused JSON and text tests before implementation
- keep text formatters in small local helpers

### Risk: Proposal Over-Commitment

Control:

- keep proposal support to summary placeholders only
- do not expose `accept` or other mutating commands in Phase 1

## Open Questions Resolved For This Phase

- Replay semantics: audit reconstruction, not re-execution
- Phase 1 scope: define proposal interfaces, implement replay only
- Run identity source: `MetaRunHistory`
- Checkpoint role: optional replay augmentation only

## Deferred Work

Leave these for later phases:

- true executable replay with persisted original inputs
- proposal records and lifecycle management
- proposal acceptance and execution
- `skill_exec` stdin capture/replay ergonomics
- richer evidence retention policies or configurable privacy controls
