# MCP Apps Host Proxy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the `d3fa5c08d679bd0036e5bbe226744a2a7db14a2b` MCP Apps host/proxy behavior into `openclaw.net` with minimal changes and regression coverage.

**Architecture:** Reuse `OpenClaw.McpApp` as the source of truth for running MCP Apps, then add gateway `/apps/*` endpoints that bridge browser-side hosts to the same connected `McpClient` session the agent uses. Align MCP App tool discovery/execution with MCP Apps visibility and UI structured-content rules.

**Tech Stack:** ASP.NET Core minimal APIs, ModelContextProtocol.AspNetCore, `OpenClaw.McpApp`, xUnit v3.

## Global Constraints

- Keep changes scoped to MCP Apps host/proxy behavior and directly related tests.
- Prefer adapting existing `OpenClaw.McpApp` and gateway wiring over introducing a parallel MCP Apps stack.
- Add tests before production changes for each new behavior slice.

---

### Task 1: MCP App discovery behavior parity

**Files:**
- Modify: `src/OpenClaw.Tests/McpAppTests.cs`
- Modify: `src/mcpapp/OpenClaw.McpApp/shared/McpAppInfoProvider.cs`
- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppServer.cs`
- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppToolProvider.cs`

- [ ] Add failing tests for app-only tool filtering and UI structured-content suppression.
- [ ] Implement MCP App metadata parsing for `_meta.ui.visibility` and `_meta.ui.resourceUri`.
- [ ] Keep only model-visible tools in the native registry surface.
- [ ] Suppress `structuredContent` for UI-bound tool results returned to the model.

### Task 2: Gateway `/apps/mcp/{appId}` host proxy

**Files:**
- Create: `src/OpenClaw.Gateway/Endpoints/AppsMcpProxyEndpoint.cs`
- Modify: `src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs`
- Modify: `src/OpenClaw.Gateway/Endpoints/EndpointMappingsExtensions.cs`
- Modify: `src/OpenClaw.Tests/AppsMcpProxyEndpointTests.cs`

- [ ] Add failing tests for tools passthrough and `_meta.sessionId` injection.
- [ ] Forward MCP host requests onto the already-connected app `McpClient`.
- [ ] Protect the route with the same loopback-or-auth rule as the source commit.

### Task 3: Gateway `/apps` host endpoints and CORS

**Files:**
- Create: `src/OpenClaw.Gateway/Endpoints/AppsEndpoints.cs`
- Modify: `src/OpenClaw.Gateway/Pipeline/PipelineExtensions.cs`

- [ ] Add `/apps/health` and `/apps/chat` endpoints that bridge chat hosts into `GatewayAppRuntime`.
- [ ] Allow browser MCP headers in CORS for cross-origin MCP host calls.
- [ ] Run focused tests for new endpoint and MCP App slices, then a targeted build if needed.