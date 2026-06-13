# Meta-Runs Proposal Review Design

Date: 2026-06-13

## Summary

Add a minimal, operator-facing proposal review workflow on top of the existing derived read-only `meta-runs proposals` surface.

This slice keeps proposal derivation unchanged and introduces a separate review record store to track operator decisions (`accept` or `dismiss`) for derived proposal IDs. The workflow is intentionally non-executing: review decisions do not trigger replay, resume, tool calls, or `LearningProposal` mutation.

## Goals

- Add explicit operator review actions for derived meta-run proposals.
- Keep existing derived proposal generation read-only and backward-compatible.
- Persist review decisions separately from session meta-run evidence.
- Surface review state in both list and show views.
- Preserve NativeAOT safety and source-generated JSON serialization behavior.

## Non-Goals

This phase does not include:

- executable replay or run resumption
- automatic action after `accept` or `dismiss`
- migration to `LearningProposal` as the authoritative store
- cross-session proposal review aggregation
- optimistic concurrency / ETag versioning for review writes

## Confirmed Product Decisions

- Storage model: independent review records (not embedded in derived proposal objects).
- Action semantics: `accept` / `dismiss` only record operator review state.
- Idempotency: repeating the same action is successful and reported as already reviewed.
- Conflict policy: opposite action after a decision is rejected.
- Visibility: review state appears in both proposals list and proposal detail.
- Dismiss reason: optional `--reason` on dismiss.

## CLI Surface

### Existing commands retained

- `openclaw skills meta-runs proposals <session-id> [--run <run-id>] [--storage <path>] [--json]`
- `openclaw skills meta-runs proposals show <session-id> --proposal <id> [--storage <path>] [--json]`

### New commands

- `openclaw skills meta-runs proposals accept <session-id> --proposal <id> [--storage <path>] [--json]`
- `openclaw skills meta-runs proposals dismiss <session-id> --proposal <id> [--reason <text>] [--storage <path>] [--json]`

## Behavioral Contracts

### Success and idempotency

- First `accept` or `dismiss` on a pending proposal writes the review decision.
- Repeating the same decision succeeds and is reported as idempotent (`alreadyReviewed=true`).
- Repeating `dismiss` with a new reason after already dismissed remains idempotent and does not overwrite the stored reason in this phase (first-write-wins for `Reason`).

### Conflict behavior

- If a proposal is already accepted, a dismiss attempt fails.
- If a proposal is already dismissed, an accept attempt fails.

### Not-found behavior

- Missing session: fail.
- Missing proposal ID in the session's derived proposal set: fail.

### Exit codes

- `0`: success (including idempotent repeat of same action)
- `1`: domain failure (session not found, proposal not found, decision conflict)
- `2`: usage/parameter error

### Output mode behavior

- `--json` success returns structured decision result payload.
- `--json` failure writes no partial JSON to stdout; errors are stderr-only.
- text success prints proposal ID, resulting review status, and idempotency indicator.

## Data Model

### Independent review DTOs

Add dedicated models in `src/OpenClaw.Core/Models/Session.cs`:

- `MetaRunProposalReviewRecord`
  - `SessionId`
  - `ProposalId`
  - `Decision` (`accepted` | `dismissed`)
  - `Reason` (nullable)
  - `ReviewedAtUtc`
  - `ReviewedBy` (nullable/reserved)

- `MetaRunProposalReviewSet`
  - `SessionId`
  - `Reviews` (array of `MetaRunProposalReviewRecord`)

- `MetaRunProposalReviewMutationResponse`
  - `SessionId`
  - `ProposalId`
  - `ReviewStatus`
  - `AlreadyReviewed`
  - `ReviewedAtUtc`
  - `Reason`

### Proposal read-view enrichment (additive)

Keep derived proposal summary/detail fields intact, add review fields only:

- List summary additive fields:
  - `ReviewStatus` (`pending` | `accepted` | `dismissed`)
  - `ReviewedAtUtc` (nullable)

- Show detail additive field:
  - `Review` object:
    - `Status`
    - `ReviewedAtUtc`
    - `Reason`

For proposals without a review record, status is `pending`.

## Persistence Design

- Use a dedicated review storage artifact scoped by session ID.
- Review reads and writes do not modify `Session.MetaRunHistory` or derived proposal evidence fields.
- `proposals` and `proposals show` resolve derived proposals first, then merge review state by proposal ID.

## Compatibility and AOT

- Existing derived list/show fields remain stable.
- New review fields are additive and optional in JSON output.
- All new DTOs are added to `CoreJsonContext` source-generation attributes.
- No reflection-based serialization paths are introduced.

## Error Strings (contract intent)

Use existing CLI style:

- Session not found
- Proposal not found in session
- Proposal already reviewed with conflicting decision
- Missing required `--proposal`

Exact wording can align with current command-family conventions, but should stay deterministic for tests.

## Test Plan

Add focused tests in `src/OpenClaw.Tests/SkillCommandsTests.cs` for:

- accept success (json and/or text)
- accept idempotent repeat success
- dismiss success with optional reason
- dismiss idempotent repeat success
- conflict rejection for opposite decision attempts
- list includes review status and reviewed timestamp
- show includes structured review section
- `--json` error paths produce empty stdout and stderr-only errors
- regression that existing derived proposal fields still appear

Then run:

- focused `RunAsync_MetaRuns_Proposals_` slice
- wider `RunAsync_MetaRuns_` slice

## Rollout Notes

This design intentionally creates a bridge layer. A later migration can map review records into `LearningProposal` lifecycle entities once proposal execution and governance semantics are ready.
