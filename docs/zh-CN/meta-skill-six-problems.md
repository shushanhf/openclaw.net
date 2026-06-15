# MetaSKILL 解决的 6 个真问题

> 从"Agent 调个工具"到"生产级多步工作流"之间的鸿沟，不是多写几行 prompt 能填平的。

---

## 问题总览

| # | 问题 | 单 Skill 能做到吗 | MetaSKILL 方案 |
|---|---|---|---|
| 1 | 长任务卡死没法停 | ❌ | `timeout_seconds` + `retry` + 合约封顶 |
| 2 | 多步任务需要人确认关键节点 | ❌ | `user_input` + `clarify` + checkpoint 暂停/恢复 |
| 3 | 复杂流程要"可审计 + 可恢复" | ❌ | `MetaRunHistory` + `replay` + `reconstruct` + `proposals` |
| 4 | 不同 Skill 之间需要编排依赖 | ❌ | `depends_on` DAG + `skill_exec`/`agent` 委托 |
| 5 | 任务失败需要 fallback 降级路径 | ❌ | `on_failure` 5 条约束 + 输出镜像 |
| 6 | 多团队复用同一任务模板 | ❌ | Meta-skill 即模板 + Session 隔离 + catalog |

---

## 问题 1：长任务卡死没法停

**现象**：Agent 调了个 LLM 或子进程，卡了 10 分钟，用户只能杀进程。

**MetaSKILL 怎么做**：

```yaml
composition:
  steps:
    - id: llm_summarize
      kind: llm_chat
      timeout_seconds: 15          # ← 15 秒超时自动取消
      retry:
        max_attempts: 2            # ← 失败再试 1 次
        backoff_ms: 500
```

四层有界执行：

```
Session 合约封顶 (MaxRuntimeSeconds)
  └─ Agent 循环 (maxIterations + circuit breaker)
       └─ Meta Step 执行 (timeout_seconds + retry)
            └─ 失败 → on_failure 替代分支 (DAG 继续)
```

关键代码路径：`CreateMetaStepTimeout` 创建 `CancellationTokenSource`，`CancelAfter` 到期自动 fire；超时异常被捕获转为 `step_timeout` 失败码，不杀整个 DAG。

---

## 问题 2：多步任务需要人确认关键节点

**现象**：生成了一份计划 → 需要人审核 → 审核通过继续执行 → 审核不通过走降级。传统 Agent 做不到"停在中间等人"。

**MetaSKILL 怎么做**：

```yaml
steps:
  - id: draft_plan
    kind: llm_chat
    with:
      task: "生成执行计划: {{ input }}"

  - id: approve_plan
    kind: user_input
    prompt: "请审核以上计划，输入 approve 或 reject"
    clarify:
      mode: form
      fields:
        - name: decision
          type: enum
          options: [approve, reject]
      cancel_words: [cancel, 取消]
      timeout_seconds: 300           # ← 5 分钟不回复自动降级
    on_failure: auto_approved        # ← 超时走自动批准

  - id: execute
    kind: skill_exec
    skill: executor
    depends_on: [approve_plan]
    skill_exec_args: ["{{ outputs.approve_plan }}"]
```

三方协同：

```
user_input 暂停 → SaveMetaExecutionCheckpoint (保存完整 DAG 状态)
    ↓
用户输入 → clarify 表单校验 (类型/必填/枚举/取消词/超时)
    ↓
校验通过 → DAG 继续 | 失败/取消/超时 → on_failure 降级
```

---

## 问题 3：复杂流程要"可审计 + 可恢复"

**现象**："上次那个任务到底在哪步失败的？""能不能看看它每一步花了多长时间？""能不能从失败点重来？"

**MetaSKILL 怎么做**：

每次执行自动写入 `Session.MetaRunHistory`：

```json
{
  "RunId": "run-20260615-001",
  "SkillName": "weekly-report",
  "Status": "failed",
  "StepResults": [
    { "Id": "gather",   "Kind": "skill_exec", "Status": "completed", "DurationMs": 1200 },
    { "Id": "summarize","Kind": "llm_chat",    "Status": "failed",    "DurationMs": 15000,
      "FailureCode": "step_timeout" }
  ]
}
```

审计命令：

```bash
openclaw skills meta-runs <session-id>                  # 列出所有 run
openclaw skills meta-runs <session-id> --run <id> --verbose  # 逐步 trace
openclaw skills meta-runs replay <session-id> --run <id>     # 回放预览
openclaw skills meta-runs reconstruct <session-id> --run <id> # 审计重建
```

恢复路径：

| 场景 | 机制 |
|---|---|
| 暂停等用户输入 | `MetaExecutionCheckpoint` 自动恢复 |
| 步骤超时/失败 | `on_failure` 替代分支 |
| 事后审查 | `replay`（预览）+ `reconstruct`（审计重建） |
| 失败驱动改进 | `proposals show` → `accept`/`dismiss` |

---

## 问题 4：不同 Skill 之间需要编排依赖

**现象**：调完 data-fetcher 拿到数据 → 分类 → 走不同处理管道 → 汇总。这是 DAG，不是线性 prompt。

**MetaSKILL 怎么做**：

```yaml
composition:
  steps:
    - id: fetch
      skill: data-fetcher
      kind: skill_exec
      skill_exec_args: ["--date", "{{ input }}"]

    - id: classify
      kind: llm_classify
      depends_on: [fetch]
      with:
        options: [urgent, normal]

    - id: urgent
      skill: code-reviewer
      kind: agent
      depends_on: [fetch]

    - id: normal
      skill: reporter
      kind: skill_exec
      depends_on: [fetch]

    - id: finalize
      kind: llm_chat
      depends_on: [urgent, normal]   # ← 等所有分支完成
```

`depends_on` → `BuildDependentsIndex`（反向依赖索引）→ `ApplyCompletionRouting`（完成后激活下游）。

输出通过 `outputs` 字典在步骤间传递：`{{ outputs.fetch }}`。

**约束**：Meta 不嵌套 Meta（`TryValidateMetaPlan` 拒绝 `kind: meta` 的 delegated skill），防止递归爆炸。

---

## 问题 5：任务失败需要 fallback 降级路径

**现象**：LLM 调用超时了，不能整个流程崩掉——得有个兜底方案，比如用模板生成降级输出。

**MetaSKILL 怎么做**：

```yaml
steps:
  - id: llm_summarize
    kind: llm_chat
    on_failure: fallback_template
    timeout_seconds: 15

  - id: fallback_template
    kind: tool_call
    tool: emit_text
    with:
      text: "Summary unavailable — using template"

  - id: publish
    skill: publisher
    depends_on: [llm_summarize]   # ← 读到的是 fallback 的输出
```

**输出镜像**：`failureAliases["fallback_template"] = "llm_summarize"`，fallback 执行完后同时写入 `outputs["llm_summarize"]`，下游读 `{{ outputs.llm_summarize }}` 无感知。

**5 条工程约束**（全部 parse-time + runtime 双层校验）：

| # | 约束 | 目的 |
|---|---|---|
| 1 | 目标 step id 必须存在 | 防止悬空引用 |
| 2 | 不能指向自己 | 防止死循环 |
| 3 | fallback 不能有 `on_failure` | 禁止链式降级 |
| 4 | 同一 fallback 只能被一个 primary 引用 | 防止并发覆盖 |
| 5 | fallback 不能有 `depends_on` | fallback 是纯函数 |

---

## 问题 6：多团队复用同一任务模板

**现象**：三个团队都要用"周报生成"流程。每人写一份 prompt？各自调参？出问题谁改谁？

**MetaSKILL 怎么做**：

一份 `SKILL.md` → 所有团队共享：

```yaml
# skills/weekly-report/SKILL.md — 一份模板，无数实例
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

每次执行在独立的 Session 上下文中：

- `outputs` 字典：每次调用创建新的局部变量
- `MetaExecutionCheckpoint`：绑定 `session.Id`
- `MetaRunHistory`：绑定 `session.Id`
- 模板参数化：通过 `{{ input }}`、`{{ outputs.X }}` 传递上下文

多团队分发：

| 途径 | 说明 |
|---|---|
| Gateway 内置 | `skills/` 目录 — 所有连接自动可用 |
| 插件目录 | `SkillsConfig.Load.PluginDirs` |
| Catalog | `openclaw skills catalog --kind meta` |
| 按团队定制 | `SkillsConfig.Entries` + `AllowBundled` 白名单 |

---

## 总结：6 个问题的本质

```
问题 1-2：执行期可靠性  (timeout + 暂停)
问题 3：   运维期可信度  (可审计 + 可恢复)
问题 4-5：编排期韧度    (DAG + fallback)
问题 6：   协作期复用性  (模板 + 隔离)
```

单 Skill 是 1 步、不能暂停、不能兜底、不能 DAG。

MetaSKILL 是 3-12 步 DAG + 暂停 + 5 条 on_failure + depends_on + meta-skill-creator + plan_serde snapshot 可审计。

> 12 步 → 拆多个 MetaSkill 或用 Microsoft Agent Framework Workflow / LangGraph。
