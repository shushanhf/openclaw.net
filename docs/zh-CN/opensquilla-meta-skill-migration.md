# OpenSquilla 元技能迁移说明

这份说明总结了当前 OpenClaw.NET 对 OpenSquilla 风格元技能路径的实现水平，重点放在“已经对齐的能力”和“还需要补齐的迁移项”。

## 当前状态

OpenClaw.NET 已经实现了 OpenSquilla 风格元技能编排的核心骨架：

- `kind: meta` 与 `composition.steps` DAG 编排
- `depends_on` 依赖顺序与循环依赖校验
- `llm_classify` 的 `options + route` 分支路由
- `user_input` 的暂停 / 恢复与会话检查点恢复
- `final_text_mode: auto | raw | structured | step:<id>`
- 结构化执行结果 envelope，便于自动化和诊断

这说明当前运行时已经可以承载“先组装技能图，再分类分支，再执行工具或模型”的基础流程。

参考 OpenSquilla 源码树 `E:\GitHub\opensquilla\src\opensquilla\skills\meta` 的基线可以看到：

- `parser.py` 把 `on_failure` 当作一类显式的失败分支契约来解析与校验，要求目标步骤存在、不能自引用、不能嵌套链式 failover、且一个 fallback 目标只能被一个主步骤占用。
- `types.py` 与 parser 层把 `output_choices`、`tool_allowlist`、`clarify` schema 当成强约束，而不是只靠运行时约定。

OpenClaw.NET 现在已经补上了与之对应的首类契约：显式失败替代分支、step 级重试 / 超时策略，以及 JSON 中间结果校验；但更完整的 OpenSquilla 元策略面仍然比当前实现更宽。

## 已经对齐的部分

### 1. DAG 编排

OpenClaw.NET 会把元技能的步骤定义为 `composition.steps`，并在解析阶段校验：

- 重复 step ID
- 缺失依赖
- 自依赖
- 依赖环
- `llm_classify` 的路由目标是否有效

这让元路径具备“先失败快、后执行”的治理能力，而不是默默接受坏图。

### 2. 步骤类型

当前运行时支持的核心编排类型包括：

- `agent`
- `skill_exec`
- `tool_call`
- `llm_chat`
- `llm_classify`
- `user_input`

这对应了当前 OpenClaw.NET 元技能执行面上的主要能力边界。

### 3. 结构化输出与诊断

当开启 `final_text_mode: structured` 时，运行时可以返回结构化载荷，包含：

- `skill`
- `final_text`
- `error` / `error_code`
- `steps[]`（状态、耗时、失败代码等）

此外，元步骤现在也支持：

- `on_failure` 替代分支，并在 parser/runtime 两层校验
- `retry` 与 `timeout_seconds`，用于工具和模型步骤的有界重试与超时
- `output_contract` / `output_schema`，用于校验 JSON 中间结果的必填字段

这有利于自动化测试、日志排障和运维观测。

## 迁移 checklist

把 OpenSquilla 元技能迁移到 OpenClaw.NET 时，建议按下面的顺序做：

1. 用 `composition.steps` 表达编排图，并用 `depends_on` 控制顺序。
2. 用 `llm_classify` 处理分支选择，而不是靠字符串判断。
3. 如果失败步骤应该激活一个替代步骤，并把替代步骤输出镜像回主步骤 ID 给下游依赖使用，就使用 `on_failure`。
4. 如果只是希望失败后继续执行、但不需要替代分支语义，再使用 `with.continue_on_error`。
5. 对需要有界执行的工具或模型步骤，配置 `retry.max_attempts`、可选的 `retry.backoff_ms`，以及 `timeout_seconds`。
6. 如果下游依赖结构化中间结果，使用 `output_contract` / `output_schema`，并声明 `format: json` 与 `required_properties`。
7. 若需要机器可读结果，优先使用 `final_text_mode: structured`。
8. 对交互式流程，使用 `user_input` 来承载暂停与恢复边界。

## 当前已知的迁移差距

当前 OpenClaw.NET 的元路径已经覆盖 DAG 执行、fail-fast 校验、显式失败替代、有界 step 执行，以及 JSON 中间结果契约；但还不是 OpenSquilla 原生元技能契约的“完全等价替代”。剩余差距主要如下：

| 差距项 | 影响 | 当前状态 |
| --- | --- | --- |
| 更完整的中间结果强类型约束 | 一些高级流程需要把 `output_choices`、`tool_allowlist`、`clarify` schema 等也视为强约束，而不是只靠运行时约定 | 已实现 JSON `output_contract` / `output_schema` 必填字段校验；其余 OpenSquilla typed contract 仍是部分对齐 |
| 更完整的元层策略开关 | 当前更聚焦于编排与执行，不是完整的元策略配置面 | 只有部分对齐 |

## 建议

把当前 OpenClaw.NET 的元技能路径理解为“强 DAG 编排 + 显式失败替代 + 有界 step 执行 + JSON 中间结果校验 + 结构化执行”的 OpenSquilla 风格实现。后续如果要继续追求更完整的 OpenSquilla parity，重点应放在 `output_choices`、`tool_allowlist`、更丰富的 `clarify` schema 处理，以及更宽的元层策略开关上。
