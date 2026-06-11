# OpenSquilla Meta-Skill Migration Notes

This note captures the current OpenClaw.NET view of the OpenSquilla-style meta-skill path, with emphasis on what is already aligned and what still needs a dedicated migration step.

## Current status

OpenClaw.NET already supports the core OpenSquilla-style meta composition model:

- `kind: meta` skills with a `composition.steps` DAG
- `depends_on` ordering and dependency-cycle validation
- `llm_classify` routing via `options` + `route`
- `user_input` pause/resume behavior with session checkpoint restoration
- `final_text_mode: auto | raw | structured | step:<id>`
- structured execution envelopes for inspection and automation

That means the current runtime is already suitable for the basic “compose a skill graph, classify, branch, and execute” pattern that OpenSquilla-style meta skills rely on.

Reference baseline from the OpenSquilla source tree at E:\GitHub\opensquilla\src\opensquilla\skills\meta:\n- `parser.py` validates `on_failure` as a first-class substitute path and enforces target existence, no self-reference, no nested failover chains, and one primary substitute per fallback target.
- `types.py` and the parser layer also treat `output_choices`, `tool_allowlist`, and `clarify` schema as typed contracts rather than loose runtime conventions.
This gives the reference path a more explicit failure-branch and contract-validation model than the current OpenClaw.NET meta path exposes today.

## What is already aligned

### 1. DAG composition

OpenClaw.NET accepts meta skills whose steps are declared as a JSON array under `composition.steps`, and validates:

- duplicate step IDs
- missing dependencies
- self-dependencies
- dependency cycles
- invalid `llm_classify` route targets

This gives the meta path a fail-fast contract instead of silently accepting broken graphs.

### 2. Step kinds

The runtime supports the main orchestration kinds used by the current meta path:

- `agent`
- `skill_exec`
- `tool_call`
- `llm_chat`
- `llm_classify`
- `user_input`

This is the main execution surface for OpenSquilla-style orchestration logic in current OpenClaw.NET.

### 3. Structured outputs and diagnostics

The runtime can return a structured envelope when `final_text_mode: structured` is enabled, including:

- `skill`
- `final_text`
- `error` / `error_code`
- `steps[]` with status, duration, and failure metadata

This makes the meta path suitable for automation, testing, and operator diagnostics.

## Migration checklist

When porting an OpenSquilla meta skill to OpenClaw.NET, use the following checklist:

1. Keep the composition in `composition.steps` and prefer `depends_on` for ordering.
2. Use `llm_classify` for branch selection, not ad-hoc string parsing.
3. Put fallback behavior under `with.continue_on_error` when you want execution to continue after a failed step.
4. Use `final_text_mode: structured` for machine-readable result contracts.
5. Keep `user_input` steps as the pause/resume boundary for interactive flows.

## Known migration gaps

The current OpenClaw.NET meta path is already strong on DAG execution and fail-fast validation, but it is not yet a full drop-in replacement for every OpenSquilla-native meta contract. The main gaps are:

| Gap | Why it matters | Current status |
| --- | --- | --- |
| Explicit `on_failure` / fallback branch policy | The OpenSquilla reference path models failure substitution as a parser-validated fallback contract. OpenClaw.NET currently still relies on generic `continue_on_error` behavior rather than a dedicated failover branch contract. | Partially aligned at the reference level; not yet implemented as a first-class OpenClaw.NET meta contract. |
| Rich retry / timeout policy at step level | Production meta flows often require per-step retries, timeouts, and backoff for tool or LLM calls. | Not yet implemented as a first-class meta-step policy. |
| Stronger typed intermediate-output contracts | The reference path treats `output_choices`, `tool_allowlist`, and `clarify` schema as typed validation boundaries. OpenClaw.NET still treats many intermediate outputs as runtime-convention payloads. | Needs a stronger parser/runtime contract layer. |
| Additional meta-level policy controls | The current runtime focuses on composition and execution, not on broader meta policy toggles. | Partial parity only. |

## Recommendation

Treat the current OpenClaw.NET meta path as a strong OpenSquilla-style DAG runtime for composition, routing, and structured execution — but not yet as the final, fully feature-equivalent migration target for every advanced meta policy.

If you need full OpenSquilla parity, the next migration work should focus on explicit failure-branch semantics, step-level retry/timeout policy, and stronger typed output contracts.
