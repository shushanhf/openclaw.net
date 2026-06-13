# OpenSquilla Meta-Skill P0 + Jinja Compatibility Design

Date: 2026-06-11

## Summary

Implement the OpenSquilla meta-skill migration blockers called out as P0 in `docs/zh-CN/opensquilla-meta-skill-migration.md`, and include the selected P1 Jinja template compatibility in the same implementation cycle.

The implementation will use a shared Core-layer design: parse and validate the DSL in `OpenClaw.Core`, implement template/condition/clarify/tool-argument behavior as reusable Core services, and have both `OpenClaw.Agent` and `OpenClaw.MicrosoftAgentFrameworkAdapter` call those shared services. This keeps meta-skill semantics consistent across runtime surfaces.

The Jinja implementation must use the package selected by the user:

```bash
dotnet add src/OpenClaw.Core/OpenClaw.Core.csproj package Jinja2.NET --version 1.4.1
```

## Goals

### P0: OpenSquilla Native DSL Compatibility

Support the native fields that directly block migration of OpenSquilla `SKILL.md` meta skills:

- `output_choices`
- composition-level `tool_args`
- step-level `tool_args`
- step-level `tool_allowlist`
- `clarify`
- step-level `when`
- OpenSquilla route arrays: `route: [{ when, to }]`

Existing OpenClaw fields and behavior must remain compatible:

- `with`
- `with.options`
- `with.route` object maps for `llm_classify`
- `output_contract` / `output_schema`
- `on_failure`
- retry and timeout policies
- `final_text_mode`
- structured execution envelopes

### P0: Full `user_input.clarify` Schema Contract

Promote `clarify` from a convention in `with` to a parser/runtime contract for `user_input` steps. Support:

- `mode: form | chat`
- field types: `string`, `integer`, `number`, `boolean`, `enum`
- defaults
- required fields
- enum options
- integer/number min/max
- string min/max length
- cancel words
- timeout
- optional natural-language extraction

### P1: Jinja Template Rendering

Replace the current minimal hard-coded template replacement with Jinja rendering through `Jinja2.NET 1.4.1`.

Template rendering must support at least:

- `{{ input }}`
- `{{ inputs.user_message }}`
- `{{ outputs.<step_id> }}`
- bracket access for step IDs that cannot be used as dot identifiers: `outputs["step-id"]`
- filters: `xml_escape`, `slugify`, `truncate`, `tojson`

## Non-Goals

This implementation does not include:

- OpenSquilla `skill_exec` entrypoint/subprocess execution semantics
- persistent meta run history, replay, proposal CLI, or audit UI
- a dedicated `[meta_skill] enabled = false` policy switch
- parallel meta-step scheduling
- product catalog, creator, proposal, or auto-enable audit workflows
- new TUI/Dashboard/Channel visual form controls

`clarify.mode: form` will be implemented as a schema contract, JSON resume format, validation, and recovery prompt. It will not create a new native visual form UI.

## Architecture

### Core-Layer Ownership

Add the new DSL model and behavior to `OpenClaw.Core`:

- `src/OpenClaw.Core/Skills/SkillModels.cs`
- `src/OpenClaw.Core/Skills/SkillLoader.cs`
- new Core services under an appropriate `Skills` or `Skills.Meta` namespace

The runtime projects will consume these Core services:

- `src/OpenClaw.Agent/AgentRuntime.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`

This avoids duplicating P0/P1 semantics across the two runtimes.

### New Shared Services

Introduce shared services/classes such as:

- `MetaExecutionContext`
  - carries `input`, `inputs`, `outputs`, and lightweight step state for template rendering and conditions
- `MetaTemplateRenderer`
  - wraps Jinja2.NET and registers OpenSquilla-compatible filters
- `MetaConditionEvaluator`
  - evaluates `when` and `route.when` using the same context and truthiness rules
- `MetaToolArgumentResolver`
  - merges composition and step tool arguments, renders templates, and validates JSON object output
- `MetaClarifySchema` / `MetaClarifyField`
  - represent parsed clarify contracts
- `MetaClarifyValidator`
  - validates user input and produces canonical JSON output for structured clarify results
- `MetaRouteDefinition`
  - represents OpenSquilla route array entries

## DSL Model Changes

### `MetaSkillComposition`

Add:

- `ToolArgsJson`
  - raw JSON from composition-level `tool_args`
  - must be a JSON object
  - acts as default args for all `tool_call` steps

### `MetaSkillStepDefinition`

Add:

- `When`
  - optional string condition evaluated after dependencies complete and before execution
- `ToolArgsJson`
  - raw JSON from step-level `tool_args`
  - must be a JSON object
- `ToolAllowlist`
  - optional string list; `step.tool` must be included when present
- `OutputChoices`
  - optional string list; constrains step output
- `Clarify`
  - optional typed clarify schema; only valid on `user_input`
- `Routes`
  - optional `IReadOnlyList<MetaRouteDefinition>` for `route: [{ when, to }]`

## Parser and Validation Rules

`SkillLoader.ParseComposition` should continue to parse existing fields, then parse the new OpenSquilla-native fields.

### Valid New Fields

At composition level:

- `tool_args`: object

At step level:

- `when`: string
- `tool_args`: object, only valid on `tool_call`
- `tool_allowlist`: non-empty string array, only valid on `tool_call`
- `output_choices`: non-empty string array
- `clarify`: object, only valid on `user_input`
- `route`: array of route entries

Also accept `with.clarify` for compatibility, but `step.clarify` wins over `with.clarify`.

### Route Validation

For OpenSquilla route arrays:

- each route item must be an object
- `to` is required and must be a non-empty string
- `to` must reference an existing step ID
- `to` must not equal the owner step ID
- `when`, if present, must be a string
- at most one fallback route may omit `when`
- route target steps are branch targets and must not run by default

Existing `with.route` object maps remain valid for `llm_classify` label routing. If a step declares both a legacy route object and a route array, the parser should reject the composition to avoid ambiguous routing.

### Clarify Validation

`clarify` rules:

- object required
- `mode` defaults to `chat`; allowed values are `chat` and `form`
- `fields` is required and non-empty for `form`; optional for simple `chat`
- field names are required and unique
- supported field types are `string`, `integer`, `number`, `boolean`, `enum`
- enum fields require non-empty `options`
- `min_length` and `max_length` must be non-negative and ordered
- numeric `min` and `max` must be ordered
- defaults must pass type and constraint validation
- `cancel_words`, if present, must be a string array
- `timeout_seconds`, if present, must be a positive integer
- `extract_natural_language`, if present, must be boolean

### Error Codes

Introduce parser/runtime error codes as needed:

- `invalid_tool_args`
- `invalid_tool_allowlist`
- `invalid_output_choices`
- `invalid_clarify_schema`
- `invalid_when_expression`
- `invalid_route`
- `invalid_route_target`
- `invalid_route_fallback`
- `invalid_route_scope`
- `template_render_failed`
- `condition_false`
- `tool_not_allowlisted`
- `invalid_output_choice`
- `invalid_user_input`
- `user_input_cancelled`
- `user_input_timeout`
- `route_evaluation_failed`

## Jinja Rendering Design

### Rendering Context

Expose these variables to templates:

```text
input                 Original meta invocation input
inputs.user_message   Same value as input, for OpenSquilla compatibility
outputs               Dictionary of completed step outputs
outputs.<step_id>     Output string for a step where dot access is valid
steps                 Lightweight step status/output view
```

Use bracket access for step IDs with characters that do not work in dot notation:

```jinja
{{ outputs["step-id"] }}
```

### Filters

Register these filters in `MetaTemplateRenderer`:

- `xml_escape`
  - escape `&`, `<`, `>`, `"`, and `'`
- `slugify`
  - lower-case, replace non-alphanumeric runs with `-`, collapse repeated dashes, trim dashes
- `truncate`
  - default length 80; accept an explicit length argument; append `...` when truncating
- `tojson`
  - serialize using `System.Text.Json` so values can be safely embedded in prompts or JSON-like templates

### Rendering Failure Behavior

Do not eagerly compile all templates at parse time. Many templates depend on runtime `outputs`.

At runtime:

- template rendering failure fails the current step
- failure code is `template_render_failed`
- `on_failure` may activate fallback
- `continue_on_error` may continue using existing semantics
- structured envelope records the failure code

### Condition Evaluation

`MetaConditionEvaluator` supports:

```yaml
when: "{{ outputs.classify == 'bug' }}"
```

and:

```yaml
when: "outputs.classify == 'bug'"
```

If a condition contains `{{` or `{%`, render it directly. Otherwise wrap as `{{ <condition> }}` before rendering.

Truthiness:

- false: empty string, `false`, `no`, `0`, `off`, `null`, `none`
- true: `true`, `yes`, `1`, `on`, and any other non-empty non-false-like string

## Runtime Semantics

### Initial Pending Set

Initial pending steps exclude:

- `on_failure` targets
- OpenSquilla route targets

Those steps only enter `pending` when the failure branch or route activates them.

### `when`

Evaluate after all dependencies complete and before executing the step.

If true, execute normally.

If false:

- remove the step from pending
- record result status `Skipped`
- use failure code `condition_false`
- do not write `outputs[step.Id]`
- block dependents by default because no output exists

### Route Arrays

After the route owner step completes successfully:

1. evaluate route entries in order
2. first true `when` wins
3. if no condition matches, a route with no `when` acts as fallback
4. add the selected `to` target to `pending`
5. block unselected route targets
6. if nothing matches and no fallback exists, block all route targets while keeping the owner completed

Route arrays should be allowed on every step kind, including `user_input`, because route decisions can depend on any step output.

### Legacy Classification Route Maps

Keep current `llm_classify` object-map route behavior:

```json
"route": {
  "bug": ["bug_step"],
  "doc": ["doc_step"]
}
```

This remains label-based routing for `llm_classify`. It does not replace OpenSquilla route arrays.

### `tool_args`

For `tool_call`, merge arguments in this order:

1. composition-level `tool_args`
2. step `with`
3. step-level `tool_args`

Later layers override earlier layers.

Then render the merged JSON through Jinja and parse it back as a JSON object. Invalid JSON after rendering fails the step with `invalid_tool_args` or `template_render_failed`.

### `tool_allowlist`

Before executing a `tool_call`:

1. keep the existing metadata capability check
2. if `tool_allowlist` exists, require `step.tool` to be included

Failure code: `tool_not_allowlisted`.

This narrows permissions and never broadens them.

### `output_choices`

For `llm_classify`:

- if `with.options` is absent, use `output_choices` as the classification options
- if both are present, require them to be equivalent or reject at load time
- keep existing classification label resolution behavior

For other step kinds:

- trim output and require exact match against one choice
- failure code: `invalid_output_choice`

`output_choices` and `output_contract` are cumulative; both must pass when both are declared.

### `user_input.clarify`

Input source priority:

1. resume invocation input
2. `with.value`
3. `with.default`
4. `with.default_input`
5. clarify field defaults
6. checkpoint pause and prompt user

If `clarify` has fields, successful output is canonical JSON object text:

```json
{
  "topic": "OpenSquilla",
  "priority": "medium"
}
```

If no fields exist, keep the current string output path for backward compatibility.

#### Chat Mode

- one string field: use the user input as that field value
- multiple fields with `extract_natural_language: false`: expect JSON object input
- multiple fields with `extract_natural_language: true`: use the configured LLM path to extract a JSON object, then validate locally

#### Form Mode

- expect JSON object input for multiple fields
- allow plain string input only when there is exactly one field
- when input is missing or invalid, checkpoint with a prompt that lists required fields, types, constraints, and defaults

#### Cancel Words

If trimmed input case-insensitively matches a cancel word:

- fail step with `user_input_cancelled`
- allow `on_failure` to handle fallback
- otherwise return an error/structured envelope

#### Timeout

Checkpoint stores a deadline when `timeout_seconds` is present.

On resume after the deadline:

- fail step with `user_input_timeout`
- allow `on_failure` fallback

## Testing Strategy

### Parser Tests

Extend `src/OpenClaw.Tests/SkillTests.cs` for:

- composition `tool_args`
- step `tool_args`
- `tool_allowlist`
- `output_choices`
- `when`
- route arrays
- clarify schema
- invalid field types and invalid constraints
- duplicate clarify fields
- invalid defaults
- missing route target
- route self-reference
- multiple fallback routes
- legacy `with.route` object map compatibility

### Core Service Tests

Add tests for:

- Jinja rendering of `input`, `inputs.user_message`, `outputs`
- bracket access for non-dot-safe step IDs
- filters: `xml_escape`, `slugify`, `truncate`, `tojson`
- condition truthiness
- clarify validation
- tool args merge and render

### Agent Runtime Tests

Extend `src/OpenClaw.Tests/AgentRuntimeTests.cs` for:

- `when` true execution
- `when` false skip and dependent blocking
- route array matched branch
- route array fallback branch
- no route match blocks branch targets
- tool args merge reaches fake tool
- tool allowlist denial
- output choice validation
- clarify JSON resume and canonical JSON output
- clarify cancel
- clarify timeout
- Jinja rendering in `llm_chat`, tool args, and final output

### MAF Runtime Tests

Extend `src/OpenClaw.Tests/MafAdapterTests.cs` for key parity paths:

- `when`
- route arrays
- Jinja rendering
- clarify validation
- tool args / allowlist

Parser and Core service tests do not need to be duplicated in MAF tests.

## Documentation Updates

Update:

- `docs/zh-CN/opensquilla-meta-skill-migration.md`
- `docs/opensquilla-meta-skill-migration.md` if it is still the English counterpart

The docs should state:

- P0 DSL compatibility is implemented
- full `user_input.clarify` schema contract is implemented with JSON resume/form semantics
- Jinja rendering uses `Jinja2.NET 1.4.1`
- UI-level visual form controls are not included
- `skill_exec` subprocess semantics and run history/replay remain future work

## Verification

Minimum verification command after implementation:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj
```

If package restore needs isolated validation:

```bash
dotnet restore src/OpenClaw.Core/OpenClaw.Core.csproj
```

## Implementation Order

1. Add package dependency and DSL models
2. Extend parser and fail-fast validation
3. Add Core services for Jinja, conditions, tool args, and clarify
4. Integrate shared services into `AgentRuntime`
5. Integrate shared services into `MafAgentRuntime`
6. Update tests
7. Update migration docs
8. Run verification
