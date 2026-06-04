# Optional Dependency Split

OpenClaw.NET keeps the default runtime local-first and NativeAOT-friendly. Optional integrations should live behind clear project or package boundaries when they add protocol-specific dependencies, provider SDKs, dynamic loading, or browser automation weight.

## Current Split

MQTT is the first native protocol surface extracted from `OpenClaw.Agent`:

- project: `src/OpenClaw.Protocols.Mqtt`
- package dependency: `MQTTnet`
- config owner: `OpenClaw.Core` still owns `MqttConfig`
- composition owner: `OpenClaw.Gateway` registers MQTT tools through `NativePluginRegistry.RegisterExternalTool(...)`
- behavior: existing `OpenClaw:Plugins:Native:Mqtt` config remains unchanged

This keeps the protocol package optional while preserving the gateway behavior operators already use.

## Remaining Boundaries

The following dependencies intentionally remain in `OpenClaw.Agent` for now:

| Surface | Current blocker | Next seam |
| --- | --- | --- |
| Browser tool / Playwright | `OpenClawToolExecutor` handles browser-specific sandbox fallback and local-execution diagnostics directly. | Move browser availability and sandbox-fallback behavior behind a protocol-neutral tool capability seam. |
| MCP tool registry | MCP registration participates in gateway startup and native registry composition. | Separate MCP registration contracts from Agent-owned tool registry implementation. |
| Plugin bridge | Plugin host, bridge process, dynamic native host, hooks, skills, providers, and diagnostics share runtime startup state. | Split bridge contracts and host lifecycle before moving transport-specific code. |
| OpenAI-specific provider packages | Provider construction still flows through current agent/runtime composition. | Keep provider-specific SDK use in provider projects or gateway composition before removing package weight from Agent. |

## Contributor Rule

Do not create empty optional projects to imply separation. Move a dependency only when the target project owns the implementation and the default runtime behavior can be validated with build, tests, and the HelloAgent smoke.

If a split requires changing public runtime semantics, document the needed seam first and keep the dependency in place until the seam is reviewed.
