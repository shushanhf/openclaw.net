# OpenClaw.NET Goal 功能实现方案

## TL;DR

本文档提供了在 **OpenClaw.NET** 中实现 **Goal（目标）命令** 的完整 PR 级方案。Goal 是一个会话级持久化目标机制，当模型停止工作时自动触发续跑检查，对抗模型的"懒惰"问题。实现包含完整的状态机、Token 预算、模型工具、CLI 命令和 TUI Footer 显示，核心续跑逻辑直接在 C# 运行时中处理。

---

## 1. 功能概述与核心原理

### 1.1 什么是 Goal

Goal 是 Agent Harness 中一个相对较新的概念，用于对抗模型在执行长程任务时的**懒惰问题**——模型可能在未完成全部工作时就停下来，或者在没有验证的情况下宣告完成。Goal 的核心机制是：当模型停止（Stop）时，系统自动触发一轮检查，基于用户设定的目标条件分析当前是否已完成，未完成则继续执行。这本质上是用户在使用 Coding Agent 时经常发送的"继续"指令的自动化版本。[^26^]

### 1.2 为什么需要 Goal

在长程任务场景中（如修复 bug、编写文档、重构代码），模型往往会在以下情况过早停止：发现部分工作未完成但选择暂停；认为已完成但未检查整个工作范围；生成的解决方案过于保守或缩小了原始目标范围。Goal 通过在每次 Stop 时强制进行完成审计（Completion Audit），确保模型持续朝最终状态推进，直到目标真正达成或遇到不可逾越的障碍。[^26^][^29^]

### 1.3 主流实现对比

| 特性 | Claude Code | Codex | OpenClaw (上游) | OpenClaw.NET (本实现) |
|------|-------------|-------|-----------------|----------------------|
| 触发机制 | Hook on Stop | Hook on Stop | Hook on Stop | **C# 运行时循环内检查** |
| 状态机 | active/achieved/failed | Active/Paused/Blocked/BudgetLimited/Complete | active/paused/blocked/budget_limited/usage_limited/complete | **同上，6 状态** |
| Token 预算 | 支持 | 支持 | 支持 | **支持** |
| 模型工具 | 无 | update_goal | get_goal/create_goal/update_goal | **3 个工具全实现** |
| CLI 命令 | /goal | /goal | /goal start/pause/resume/... | **完整 CLI 命令集** |
| TUI 显示 | Footer | Footer | Footer | **Footer + Panel** |
| 完成审计 | 有 | 有 | 有 | **有（Prompt 注入）** |
| Block 阈值 | 隐式 | 3 次重复 | 3 次重复 | **3 次重复** |

### 1.4 OpenClaw.NET 的集成策略

与上游 OpenClaw 和 Claude Code 不同，OpenClaw.NET 的 Agent 循环是通过 `IChatClient` (Microsoft.Extensions.AI) 驱动的纯 C# 运行时。Goal 的续跑逻辑直接嵌入在 `AgentRuntime.RunAsync()` 和 `RunStreamingAsync()` 的 iteration 循环中：当 `toolCalls.Count == 0`（模型停止）时，检查是否存在 active goal，若存在则向消息列表注入 goal-check system prompt 并 `continue` 进入下一轮迭代。这种方式**不依赖外部 Hook 系统**，完全在运行时内完成控制流转。[^39^]

---

## 2. 架构设计

### 2.1 整体架构图

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           User / Operator                                │
│         /goal start "fix CI for PR 87469"  +500k                        │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                        OpenClaw.Cli (GoalCommands)                       │
│  /goal start │ /goal pause │ /goal resume │ /goal complete │ /goal clear  │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      IGoalService (InMemoryGoalService)                  │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────────────┐ │
│  │  CreateGoal │  │ UpdateStatus │  │ TokenTracking│  │  IterationCount │ │
│  └─────────────┘  └─────────────┘  └─────────────┘  └─────────────────┘ │
│                         ConcurrentDictionary<string, SessionGoal>         │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                    ┌───────────────┼───────────────┐
                    ▼               ▼               ▼
┌───────────────────────┐ ┌──────────────┐ ┌─────────────────┐
│   Goal Tools (3)      │ │ Goal Hook    │ │  TUI Footer     │
│ get_goal              │ │ (Continuation│ │  Display        │
│ create_goal           │ │  Prompt Inj.)│ │                 │
│ update_goal           │ │              │ │                 │
└───────────────────────┘ └──────────────┘ └─────────────────┘
                    │               │               │
                    └───────────────┼───────────────┘
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                         AgentRuntime (Turn Loop)                         │
│  for (i = 0; i < _maxIterations; i++)                                   │
│  {                                                                      │
│      LLM call → response                                                │
│      if (toolCalls.Count == 0)  // Model STOPPED                        │
│      {                                                                  │
│          if (goalActive)                                                │
│          {                                                              │
│              Inject goal-check system prompt                            │
│              continue;  // ← AUTO-CONTINUE                              │
│          }                                                              │
│          return finalResponse;  // Normal stop                          │
│      }                                                                  │
│      Execute tools → feed back to LLM                                   │
│  }                                                                      │
└─────────────────────────────────────────────────────────────────────────┘
```

### 2.2 核心组件职责

| 组件 | 文件路径 | 职责 |
|------|----------|------|
| `GoalStatus` | `OpenClaw.Core/Models/Goal/GoalStatus.cs` | 6 状态枚举 + 名称解析 |
| `SessionGoal` | `OpenClaw.Core/Models/Goal/SessionGoal.cs` | Goal 数据模型（JSON 序列化、预算计算） |
| `IGoalService` | `OpenClaw.Core/Abstractions/IGoalService.cs` | 服务接口（创建、更新、清除、Token 追踪） |
| `InMemoryGoalService` | `OpenClaw.Core/Services/InMemoryGoalService.cs` | 线程安全的内存实现（ConcurrentDictionary） |
| `GetGoalTool` | `OpenClaw.Agent/Tools/GoalTools.cs` | 模型工具：读取当前 goal 状态 |
| `CreateGoalTool` | `OpenClaw.Agent/Tools/GoalTools.cs` | 模型工具：创建 goal（需显式请求） |
| `UpdateGoalTool` | `OpenClaw.Agent/Tools/GoalTools.cs` | 模型工具：标记 complete/blocked |
| `GoalContinuationHook` | `OpenClaw.Agent/Goal/GoalContinuationHook.cs` | 续跑逻辑（Prompt 构建、预算检查、迭代计数） |
| `GoalPromptTemplates` | `OpenClaw.Agent/Goal/GoalPromptTemplates.cs` | Prompt 模板（激活、检查、TUI 格式化） |
| `AgentRuntimeGoalIntegration` | `OpenClaw.Agent/AgentRuntime.GoalIntegration.cs` | AgentRuntime 集成点封装 |
| `GoalCommand` | `OpenClaw.Cli/GoalCommands.cs` | CLI 命令实现（Spectre.Console） |
| `GoalTuiExtensions` | `OpenClaw.Tui/GoalTuiExtensions.cs` | TUI Footer 和 Panel 渲染 |

---

## 3. 状态机设计

### 3.1 状态定义与转换规则

OpenClaw.NET 的 Goal 状态机包含 **6 个状态**，与上游 OpenClaw 保持一致：[^29^]

| 状态 | 含义 | 允许转换到 | 触发条件 |
|------|------|-----------|---------|
| **Active** | 正在推进目标 | Paused, Blocked, BudgetLimited, Complete | 默认状态；/goal resume 恢复 |
| **Paused** | 操作员暂停 | Active | /goal pause |
| **Blocked** | 遇到真实阻塞 | Active | 模型/操作员标记 blocked；相同阻塞条件重复 3+ 次 |
| **BudgetLimited** | Token 预算耗尽 | Active | Token 使用达到预算上限 |
| **UsageLimited** | 用量限制（预留） | Active | 系统级用量限制触发 |
| **Complete** | 目标达成（终态） | — | 模型调用 update_goal complete 或 /goal complete |

状态转换图：

```
                    ┌─────────────┐
         ┌─────────│   Active    │◄────────┐
         │         └──────┬──────┘         │
         │                │                │
    /goal pause    budget exceeded    /goal resume
         │                │                │
         ▼                ▼                │
   ┌─────────┐     ┌─────────────┐        │
   │ Paused  │     │BudgetLimited│        │
   └────┬────┘     └──────┬──────┘        │
        │                 │               │
        └─────────────────┘               │
              /goal resume                │
                                          │
        ┌─────────────────────────────────┘
        │
   update_goal blocked              update_goal complete
        │                                │
        ▼                                ▼
   ┌─────────┐                     ┌──────────┐
   │ Blocked │                     │ Complete │
   └────┬────┘                     └──────────┘
        │
   /goal resume
```

### 3.2 Block 判定规则

模型调用 `update_goal` 设置 `blocked` 状态时必须满足严格条件（参考 Codex 设计）：[^26^]

- **不能**在第一次遇到阻塞时就标记 blocked
- 只有当**相同的阻塞条件**在连续至少 **3 个 goal turn** 中重复出现时（包括原始 turn 和任何自动 goal 续跑），才能标记 blocked
- 如果用户 resume 了一个 previously blocked 的 goal，将 resumed run 视为**新的 blocked 审计**
- 如果在 resumed run 中相同的阻塞条件再次连续重复 3+ 次，则再次标记 blocked
- 一旦满足 blocked 阈值，**不要**在保持 goal active 的同时继续报告 blocked——必须调用 `update_goal` 设置状态

---

## 4. Token 预算系统

### 4.1 预算机制

Goal 的 Token 预算是可选的正整数。预算从 goal 创建时的会话 Token 快照作为**基线**（baseline），后续所有 Token 消耗都从此基线计算，确保 goal 不会"追溯"计费创建前的 Token 使用。[^29^]

```csharp
// 创建 goal 时记录基线
var tokensAtStart = session.GetTotalTokens();
var goal = _goalService.CreateGoal(sessionId, objective, tokenBudget, tokensAtStart);

// 每次 LLM 调用后更新使用
_goalService.UpdateTokenUsage(sessionId, session.GetTotalTokens());
// 实际使用 = currentTotal - tokensAtStart
```

### 4.2 预算超限处理

当 `TokensUsed >= TokenBudget` 时：
1. Goal 状态自动转换为 **BudgetLimited**
2. 当前 turn 正常完成（返回已生成的文本）
3. 后续 turn 不再自动续跑（BudgetLimited 不是 pursuable 状态）
4. 用户可通过 `/goal resume` 重新激活（例如增加了预算）

### 4.3 预算 CLI 语法

支持自然语言的预算表达（参考 Claude Code）：

| 输入 | 解析结果 |
|------|---------|
| `/goal start fix bug +500k` | 预算 = 500,000 |
| `/goal start write docs spend 2M tokens` | 预算 = 2,000,000 |
| `/goal start refactor +1.5M` | 预算 = 1,500,000 |
| `/goal start debug issue` | 预算 = 0（无限制） |

---

## 5. 模型工具设计

### 5.1 工具清单

OpenClaw.NET 向模型暴露 **3 个 goal 工具**，与上游 OpenClaw 一致：[^29^]

| 工具 | 权限 | 功能 |
|------|------|------|
| `get_goal` | 只读 | 读取当前 session goal 的状态、目标、Token 使用、预算 |
| `create_goal` | 创建（受限） | 仅当用户/系统/开发者显式请求时创建 goal；失败如果已有 goal |
| `update_goal` | 更新（受限） | 仅允许设置 `complete` 或 `blocked`；不能 pause/resume/clear |

### 5.2 权限控制设计

模型的权限被有意限制，以防止模型静默改变目标：[^29^]

- **模型可以**：读取 goal、创建 goal（需显式请求）、标记 complete/blocked
- **模型不可以**：pause、resume、clear、替换 goal
- **操作员可以**：通过 `/goal` 命令执行任何状态转换
- **系统可以**：自动转换 BudgetLimited、清除 goal（on `/new` 或 `/reset`）

### 5.3 update_goal 的严格审计

`update_goal` 的 Prompt 说明中嵌入了严格的完成审计要求（参考 Codex 的完成审计设计）：[^26^]

> "Set status to 'complete' only when the objective has actually been achieved and no required work remains."
> "Set status to 'blocked' only when the same blocking condition has repeated for at least three consecutive goal turns."

---

## 6. AgentRuntime 集成细节

### 6.1 集成点总览

Goal 续跑逻辑需要修改 `AgentRuntime` 的 **3 个位置**：

1. **构造函数**：注入 `IGoalService` 和 `AgentRuntimeGoalIntegration`
2. **Turn 开始**：注入 goal activation system prompt
3. **Iteration 结束**：检查 goal 续跑条件，注入 goal-check prompt

### 6.2 核心代码修改

#### 6.2.1 构造函数添加依赖

```csharp
public sealed class AgentRuntime : IAgentRuntime
{
    // ... existing fields ...
    private readonly IGoalService? _goalService;
    private readonly AgentRuntimeGoalIntegration? _goalIntegration;

    public AgentRuntime(
        // ... existing parameters ...
        IGoalService? goalService = null)
    {
        // ... existing init ...
        _goalService = goalService;
        _goalIntegration = goalService is not null
            ? new AgentRuntimeGoalIntegration(goalService, logger)
            : null;
    }
}
```

#### 6.2.2 Turn 开始时注入 Goal Activation Prompt

在 `RunAsync` 和 `RunStreamingAsync` 中，构建完 `messages` 后、进入 iteration 循环前：

```csharp
var messages = BuildMessages(session, exactLatestToolBatch: resumeCheckpoint is not null);
// ... recall injection ...

// Inject goal activation prompt if a goal is active
if (_goalIntegration is not null)
{
    var goalPrompt = _goalIntegration.BuildGoalSystemPrompt(session.Id);
    if (goalPrompt is not null)
        messages.Insert(1, new ChatMessage(ChatRole.System, goalPrompt));
}
```

`messages.Insert(1, ...)` 将 goal prompt 放在 system prompt 之后、用户消息之前，确保模型优先看到目标指令。

#### 6.2.3 Iteration 结束时检查 Goal 续跑

在 iteration 循环中，当 `toolCalls.Count == 0`（模型停止）时：

```csharp
if (toolCalls.Count == 0)
{
    var text = _redaction.Redact(response.Text ?? "");

    // ── Goal continuation check ──
    if (_goalIntegration is not null)
    {
        _goalIntegration.UpdateGoalTokenUsage(session);
        var continuationPrompt = _goalIntegration.EvaluateGoalContinuation(session, i);
        if (continuationPrompt is not null)
        {
            // Inject goal-check prompt as system message and continue loop
            messages.Add(new ChatMessage(ChatRole.System, continuationPrompt));
            session.History.Add(new ChatTurn
            {
                Role = "system",
                Content = $"[goal_check:{goal?.Iterations}] Continue working toward objective..."
            });
            continue; // ← 关键：继续下一轮迭代，不返回
        }
    }
    // ── Normal final response ──
    session.History.Add(new ChatTurn { Role = "assistant", Content = text });
    MarkCheckpointCompleted(session, SessionCheckpointStates.Completed, "final_response");
    AppendContractSnapshot(session, "active");
    LogTurnComplete(turnCtx);
    return text;
}
```

### 6.3 续跑流程时序图

```
User: "fix the bug in auth module"
  │
  ▼
AgentRuntime.RunAsync()
  │
  ├──► messages = [system, user_msg]
  │    InjectGoalActivation() → messages = [system, goal_prompt, user_msg]
  │
  ├──► Iteration 0: LLM call
  │    Model: thinks, uses tools (search, read, edit)
  │    toolCalls.Count > 0 → execute tools, continue loop
  │
  ├──► Iteration 1: LLM call
  │    Model: "I've fixed the bug." (no tool calls, STOP)
  │    toolCalls.Count == 0
  │    │
  │    EvaluateGoalContinuation()
  │    ├── Goal active? YES
  │    ├── Budget exceeded? NO
  │    ├── Iterations < Max? YES
  │    └── → Inject goal-check prompt
  │
  ├──► Iteration 2: LLM call (with goal-check prompt)
  │    Model: reviews work, runs tests
  │    toolCalls.Count > 0 → execute tests
  │
  ├──► Iteration 3: LLM call
  │    Model: "Tests pass, bug is fixed." (STOP)
  │    toolCalls.Count == 0
  │    │
  │    EvaluateGoalContinuation()
  │    ├── Goal active? YES
  │    └── → Inject goal-check prompt
  │
  ├──► Iteration 4: LLM call (with goal-check prompt)
  │    Model: "Let me verify the fix is complete..."
 │         → uses update_goal(status="complete")
  │    toolCalls.Count > 0 → execute update_goal
  │    Goal status → Complete
  │
  ├──► Iteration 5: LLM call
  │    Model: "Goal complete. Here's what was done: ..."
 │         (STOP, goal is now Complete)
  │    EvaluateGoalContinuation()
  │    ├── Goal active? NO (Complete is terminal)
  │    └── → return final response
  │
  └──► Return: "Goal complete. Fixed the auth bug by..."
```

---

## 7. CLI 命令实现

### 7.1 命令参考

| 命令 | 功能 | 示例 |
|------|------|------|
| `/goal` 或 `/goal status` | 显示当前 goal 详情 | `/goal` |
| `/goal start <objective>` | 创建新 goal | `/goal start fix CI for PR 87469` |
| `/goal set <objective>` | `start` 的别名 | `/goal set refactor auth module` |
| `/goal create <objective>` | `start` 的别名 | `/goal create write API docs` |
| `/goal pause [note]` | 暂停 active goal | `/goal pause waiting for review` |
| `/goal resume [note]` | 恢复 paused/blocked/... goal | `/goal resume review done` |
| `/goal complete [note]` | 标记 goal 完成 | `/goal complete tests pass` |
| `/goal done [note]` | `complete` 的别名 | `/goal done shipped` |
| `/goal block [note]` | 标记 goal 阻塞 | `/goal block need API key` |
| `/goal blocked [note]` | `block` 的别名 | `/goal blocked DB is down` |
| `/goal clear` | 清除当前 goal | `/goal clear` |

### 7.2 关键约束

- **单 goal 限制**：一个 session 同时只能有一个 goal。创建第二个会失败，直到当前 goal 被清除。
- **`/new` 和 `/reset` 清除 goal**：这些命令会清除当前 session 的 goal，因为它们有意开始新的会话上下文。[^29^]
- **终态不可修改**：Complete 的 goal 不能被进一步修改，必须先 `/goal clear`。

---

## 8. TUI Footer 显示

### 8.1 Footer 格式规范

TUI Footer 保持紧凑，遵循上游 OpenClaw 的格式：[^29^]

| Goal 状态 | Footer 文本 |
|-----------|------------|
| Active (有预算) | `Pursuing goal (12k/50k)` |
| Active (无预算) | `Pursuing goal: fix CI for PR 874...` |
| Paused | `Goal paused (/goal resume)` |
| Blocked | `Goal blocked (/goal resume)` |
| BudgetLimited | `Goal unmet (50k/50k)` |
| UsageLimited | `Goal hit usage limits (/goal resume)` |
| Complete | `Goal achieved (42k)` |

### 8.2 集成方式

在 `TerminalUi.cs` 的 footer 渲染逻辑中，调用 `GoalTuiExtensions.FormatGoalFooterLine()` 获取 goal 状态字符串，追加到现有的 footer 元素（agent、session、model、token counts）之后。

---

## 9. Prompt 设计

### 9.1 Goal Activation Prompt

当 goal 被激活时注入的 system prompt（在 turn 开始时一次性注入）：

> **Active Goal**  
> A session-scoped goal is now active with the following objective:  
> `<objective>{{objective}}</objective>`  
>  
> **Your Behavior**  
> - Treat the objective itself as your directive. Do NOT pause to ask the user what to do.  
> - The system will automatically continue you if you stop before the goal is achieved.  
> - When the goal is fully achieved, use the update_goal tool with status='complete'.  
> - If you're genuinely blocked after repeated attempts, use update_goal with status='blocked'.  
>  
> **Completion Audit**  
> Before declaring the goal complete, derive concrete requirements from the objective. For each requirement, identify authoritative evidence. Uncertain evidence means NOT achieved.

### 9.2 Goal Check Prompt

当模型 Stop 时注入的 system prompt（每次续跑时注入）：

> **Goal Check — Continue Working**  
> You were working toward this objective: `<objective>{{objective}}</objective>`  
>  
> 1. REVIEW all work done so far  
> 2. DETERMINE whether the objective has been FULLY achieved  
> 3. If ACHIEVED → use update_goal tool with status='complete'  
> 4. If NOT ACHIEVED → CONTINUE working without asking the user  
>  
> **Budget**: Used {{tokens_used}} / Budget {{token_budget}} / Remaining {{remaining_tokens}}  
> **Fidelity**: Optimize for movement toward the requested end state. Do NOT substitute easier solutions.  
> **Blocked Audit**: Only mark blocked after 3+ consecutive turns with the same blocker.  
> Iteration: {{iteration}}/{{max_iterations}}

---

## 10. 文件清单与注册

### 10.1 新增文件

| # | 文件路径 | 说明 |
|---|----------|------|
| 1 | `src/OpenClaw.Core/Models/Goal/GoalStatus.cs` | 状态枚举 + 名称解析 |
| 2 | `src/OpenClaw.Core/Models/Goal/SessionGoal.cs` | Goal 数据模型 |
| 3 | `src/OpenClaw.Core/Abstractions/IGoalService.cs` | 服务接口 |
| 4 | `src/OpenClaw.Core/Services/InMemoryGoalService.cs` | 内存服务实现 |
| 5 | `src/OpenClaw.Agent/Tools/GoalTools.cs` | 3 个模型工具 |
| 6 | `src/OpenClaw.Agent/Goal/GoalContinuationHook.cs` | 续跑 Hook |
| 7 | `src/OpenClaw.Agent/Goal/GoalPromptTemplates.cs` | Prompt 模板 + 格式化 |
| 8 | `src/OpenClaw.Agent/AgentRuntime.GoalIntegration.cs` | AgentRuntime 集成封装 |
| 9 | `src/OpenClaw.Cli/GoalCommands.cs` | CLI 命令 |
| 10 | `src/OpenClaw.Tui/GoalTuiExtensions.cs` | TUI 扩展 |

### 10.2 修改文件

| # | 文件路径 | 修改内容 |
|---|----------|---------|
| 1 | `src/OpenClaw.Agent/AgentRuntime.cs` | 构造函数注入 IGoalService；RunAsync/RunStreamingAsync 添加 Goal 注入点和续跑检查 |
| 2 | `src/OpenClaw.Core/Models/Session.cs` | 可选：添加 `SessionGoal? CurrentGoal` 导航属性 |
| 3 | `src/OpenClaw.Cli/Program.cs` | 注册 GoalCommand 到 CLI parser |
| 4 | `src/OpenClaw.Tui/TerminalUi.cs` | Footer 渲染添加 Goal 状态行 |
| 5 | `src/OpenClaw.Gateway/` | DI 注册：IGoalService → InMemoryGoalService；注册 Goal Tools |

### 10.3 DI 注册示例

```csharp
// In Gateway or Bootstrap configuration
services.AddSingleton<IGoalService, InMemoryGoalService>();

// Goal tools are registered alongside other tools
services.AddSingleton<ITool, GetGoalToolWithContext>();
services.AddSingleton<ITool, CreateGoalTool>();
services.AddSingleton<ITool, UpdateGoalTool>();

// AgentRuntime gets IGoalService via constructor
services.AddTransient<AgentRuntime>(sp => new AgentRuntime(
    chatClient: sp.GetRequiredService<IChatClient>(),
    tools: sp.GetRequiredService<IEnumerable<ITool>>().ToList(),
    // ... other params ...
    goalService: sp.GetService<IGoalService>()
));
```

---

## 11. 测试策略

### 11.1 单元测试覆盖

| 测试类 | 覆盖内容 |
|--------|---------|
| `InMemoryGoalServiceTests` | CRUD、状态转换、并发安全、Token 预算计算 |
| `GoalStatusTests` | 枚举解析、名称映射、状态转换有效性 |
| `GoalContinuationHookTests` | ShouldContinueAfterStop 各种条件组合、Prompt 构建 |
| `GoalToolsTests` | get_goal/create_goal/update_goal 工具执行、权限边界 |
| `GoalPromptTemplatesTests` | TUI Footer 格式化、Prompt 模板变量替换 |

### 11.2 集成测试场景

1. **完整 goal 生命周期**：start → active → (auto-continue × N) → complete → clear
2. **Budget 超限**：start +500k → 工作到 budget → auto-transition to BudgetLimited → resume
3. **Blocked 审计**：start → 遇到阻塞 × 3 → update_goal blocked → resume → 解决 → complete
4. **Session 重置**：/new 或 /reset 清除 goal
5. **并发安全**：多线程同时操作同一个 session 的 goal

---

## 12. 与上游 OpenClaw 的兼容性

### 12.1 功能对等性

本实现与上游 OpenClaw 的 Goal 功能保持 **100% 行为对等**：

- 相同的 6 状态状态机
- 相同的 3 个模型工具（get_goal, create_goal, update_goal）
- 相同的 CLI 命令集
- 相同的 TUI Footer 格式
- 相同的 Token 预算机制
- 相同的 Block 审计规则（3 次重复）
- 相同的完成审计要求

### 12.2 差异点

| 方面 | 上游 OpenClaw | OpenClaw.NET 本实现 |
|------|--------------|---------------------|
| 运行时语言 | Go / TypeScript | C# / .NET |
| Hook 机制 | 外部 Hook 系统 | C# iteration 循环内检查 |
| Agent 抽象 | 自有抽象 | Microsoft.Extensions.AI IChatClient |
| 序列化 | JSON | System.Text.Json (source-generator friendly) |
| 并发模型 | goroutines / async | async/await + ConcurrentDictionary |
| NativeAOT | N/A | **完全兼容**（无 reflection，无 dynamic） |

---

## 13. 风险与缓解

| 风险 | 影响 | 缓解措施 |
|------|------|---------|
| 无限续跑循环 | 高 | MaxIterations (默认 50) + per-turn limit (默认 10) |
| Token 预算溢出 | 中 | 每次 LLM 调用后检查预算；超限即转换 BudgetLimited |
| Prompt 注入攻击 | 低 | Goal objective 限制 4000 字符；通过 redaction pipeline |
| 并发数据竞争 | 低 | ConcurrentDictionary + 原子操作 |
| NativeAOT 兼容性 | 低 | 所有代码使用 source-generator friendly 模式；无 reflection |
| 模型滥用 update_goal | 中 | 权限限制：模型只能设置 complete/blocked；不能 pause/resume/clear |

---

## 14. 后续扩展方向

1. **分布式 Goal Store**：将 `InMemoryGoalService` 替换为 Redis/SQL 实现，支持多实例部署
2. **Review Agent**：在 goal-check 时使用单独的 review agent 进行完成判定，提高准确性
3. **Goal 历史记录**：持久化已完成的 goal 历史，支持回顾和分析
4. **Goal 模板**：预定义的 goal 模板（如 "Fix bug", "Write docs", "Refactor module"）
5. **子 Goal 支持**：将大目标分解为可追踪的子目标

---

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope & strategy | 1 | CLEAR | 3 proposals, 3 accepted, 0 deferred |
| Codex Review | `/codex review` | Independent 2nd opinion | 1 | ISSUES | 20 findings, 3 critical gaps resolved |
| Eng Review | `/plan-eng-review` | Architecture & tests (required) | 1 | ISSUES | 7 findings across 4 sections |
| Design Review | `/plan-design-review` | UI/UX gaps | 1 | N/A (no UI) | Plan has no UI scope |
| DX Review | `/plan-devex-review` | Developer experience gaps | 0 | — | — |

- **CODEX:** 20 independent findings. 3 critical gaps resolved via decision (multi-channel gating, token accounting fallback, detection heuristic acceptance).
- **CROSS-MODEL:** Both models agree on approach (full upstream parity). Codex pushed harder on stop-reason detection limits and channel awareness — both accepted as plan modifications.
- **UNRESOLVED:** 0
- **VERDICT:** CEO + ENG REVIEWED — scope expansions accepted, architecture decisions confirmed. Ready for implementation.
