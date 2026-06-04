---
name: Plugin compatibility
about: Report a plugin, skill package, bridge, or optional extension compatibility issue
title: "[Plugin Compatibility]: "
labels: ["plugin-compatibility", "compatibility", "bug"]
assignees: ""
---

## What failed?

- [ ] Plugin/package discovery
- [ ] `SKILL.md` loading
- [ ] `api.registerTool()`
- [ ] `api.registerService()`
- [ ] JIT-only channel/command/hook/provider surface
- [ ] Native dynamic .NET plugin
- [ ] Optional protocol/provider package
- [ ] Other

## Runtime mode

- `OpenClaw:Runtime:Mode`: `auto` / `aot` / `jit`
- Dynamic code available: yes / no / unsure
- NativeAOT publish: yes / no

## Environment

- OS:
- Architecture:
- .NET SDK version:
- Node.js version, if using JS/TS plugins:
- Commit SHA:

## Plugin or package

- Name:
- Version or commit:
- Source path or package URL:
- Uses TypeScript: yes / no
- Requires `jiti`: yes / no / unsure

## Command or startup path

```bash
paste command here
```

## Expected result

## Actual result

```text
paste diagnostics, logs, or error output here
```

## Scope and disclosure

- Does this directly support a company, customer, or downstream commercial product use case?
- Does this request introduce a vendor-specific default, proprietary dependency, or customer-specific workflow?

Please redact secrets, tokens, private URLs, internal hostnames, and customer-identifying details.
