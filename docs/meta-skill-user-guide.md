# OpenClaw.NET MetaSkill User Guide

MetaSkill turns repeated multi-step AI collaboration patterns into reusable,
triggerable, auditable, and improvable task protocols.

A normal conversation solves one request. A MetaSkill preserves a way of doing
high-value work — with `depends_on` DAG execution, `on_failure` fallback
branches, `user_input` pause points, and full audit trails.

## What It Is

OpenClaw.NET is a .NET-native AI agent runtime and gateway. MetaSkill is its
task-protocol layer. A MetaSkill does not introduce new execution atoms — it
defines how to organize existing atoms (skills, tools, LLM calls, sub-agents)
into a reusable DAG.

```yaml
# SKILL.md
name: weekly-report
kind: meta
triggers: ["生成周报", "weekly report"]
composition:
  steps:
    - id: gather
      skill: git-log
      kind: skill_exec
    - id: summarize
      kind: llm_chat
      depends_on: [gather]
```

MetaSkill provides four core advantages:

- **Protocolized**: captured in a `SKILL.md` file with `kind: meta` and
  `composition.steps`
- **Triggerable**: activated by user intent in natural language
- **Auditable**: every step timed, every failure coded, every run recorded
- **Improvable**: repeated patterns become proposals via the
  `meta-skill-creator`

## User Mental Model

A strong MetaSkill request contains four elements:

1. **Outcome**: what you want to receive
2. **Context**: materials, entities, time range, constraints
3. **Standard**: what "good" means for this task
4. **Boundaries**: what must not happen, what must not be invented

Example:

```text
Use meta-skill `meta-document-to-decision`.

I need a decision memo, not a generic explanation.
Use only the contract terms I pasted unless you can cite sources.
Separate facts, assumptions, risks, and next actions.
Do not invent missing dates, and do not sign or send anything for me.
```

## Two Ways to Activate

### Natural Delegation

Describe the outcome directly. OpenClaw.NET matches your intent to the best
MetaSkill by trigger phrases and `meta_priority`.

```text
Generate a weekly report from my last 7 days of commits.
```

### Explicit Delegation

Name the MetaSkill directly. Best for important, expensive, or easily confused
tasks.

```text
Use meta-skill `weekly-report`.

Generate a weekly report from my last 7 days of commits. Include team
contributions, key merges, and blockers.
```

## Requirements & Setup

Check the Skill detail before running workflows. Common requirements:

- **LaTeX/PDF**: `meta-paper-write` needs `xelatex` and `bibtex` on `PATH`
- **Video**: `meta-short-drama` needs `ffmpeg` and `ffprobe`
- **Document export**: child skills (`docx`, `xlsx`, `pdf-toolkit`) need Python
  packages
- **Network**: search/image/video steps may need API keys

## Inspect Run History

List runs for a session:

```sh
openclaw skills meta-runs <session-id>
openclaw skills meta-runs <session-id> --run <run-id> --verbose
openclaw skills meta-runs <session-id> --json
```

Preview replay:

```sh
openclaw skills meta-runs replay <session-id> --run <run-id>
```

Audit reconstruction (no re-execution):

```sh
openclaw skills meta-runs reconstruct <session-id> --run <run-id>
```

## Proposals

Meta-skill creation workflows write proposals before they become installed
skills:

```sh
openclaw skills meta-runs proposals
openclaw skills meta-runs proposals show <session-id> --proposal <id>
```

Review lifecycle actions:

```sh
openclaw skills meta-runs proposals accept <session-id> --proposal <id>
openclaw skills meta-runs proposals dismiss <session-id> --proposal <id>
openclaw skills meta-runs proposals rollback <session-id> --proposal <id>
```

## Safety

MetaSkill outputs are reviewable work products and decision-support drafts. They
are **not** final professional advice in legal, medical, financial, hiring,
academic, security, or other high-stakes contexts.

Actions such as publishing, applying, installing, paying, signing, messaging, or
modifying production systems require explicit user authorization and remain the
user's responsibility.

## Discover MetaSkills

```sh
openclaw skills catalog --kind meta            # List all meta-skill templates
openclaw skills catalog --kind meta --json     # Machine-readable
openclaw skills inspect <skill-name>           # Inspect composition structure
```

---

[Site Map](SITE_MAP.md) · [Getting Started](GETTING_STARTED.md) · [Authoring Guide](authoring/meta-skills.md)
