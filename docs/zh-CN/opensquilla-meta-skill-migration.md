# OpenSquilla 元技能迁移说明

这份说明总结了当前 OpenClaw.NET 对 OpenSquilla 风格元技能路径的实现水平，重点放在已经对齐的能力和还需要补齐的迁移项。

## 当前状态

OpenClaw.NET 已经实现了 OpenSquilla 风格元技能编排的核心骨架：

- `kind: meta` 与 `composition.steps` DAG 编排
- `depends_on` 依赖顺序与循环依赖校验
- `llm_classify` 的 `options + route` 分支路由
- `user_input` 的暂停 / 恢复与会话检查点恢复
- `final_text_mode: auto | raw | structured | step:<id>`
- 结构化执行结果 envelope，便于自动化和诊断

这说明当前运行时已经可以承载“先组装技能图，再分类分支，然后执行工具或模型步骤”的基础流程。

参考 OpenSquilla 源码树 `E:\GitHub\opensquilla\src\opensquilla\skills\meta` 的基线可以看到：

- `parser.py` 把 `on_failure` 当作一类显式的失败分支契约来解析与校验，要求目标步骤存在、不能自引用、不能嵌套链式 failover、且一个 fallback 目标只能被一个主步骤占用。
- `types.py` 与 parser 层把 `output_choices`、`tool_allowlist`、`clarify` schema 当成强类型契约，而不是只靠运行时约定。

OpenClaw.NET 现在已经补上了与之对应的首类本地能力：显式失败替代分支、step 级重试 / 超时策略、JSON 中间结果校验、下文所述的 P0 原生 DSL / Jinja 兼容层，以及 P1 首轮运行时对齐切片中的 `skill_exec`、meta-run 持久化和专用 meta policy gating。这一已交付切片现已端到端实现完成，并通过 OpenClaw 测试项目整体验证（`1907 passed, 0 failed, 0 skipped`）。但更完整的 OpenSquilla 元策略面仍然比当前 OpenClaw.NET 实现更宽。

## 新近完成的对齐项

- OpenSquilla 原生 DSL 字段现在已经成为首类 parser/runtime 契约：`output_choices`、composition `tool_args`、step `tool_args`、`tool_allowlist`、`clarify`、`when`、route arrays。
- `user_input.clarify` 现在会校验 typed chat/form 输入，并把成功的多字段结果标准化为 canonical JSON 文本。
- Jinja 渲染现在使用 `Jinja2.NET 1.4.1`，并提供 OpenSquilla 兼容的 `xml_escape`、`slugify`、`truncate`、`tojson` 过滤器。
- 运行时对齐的最后一轮加固也已经完成：包括陈旧 checkpoint 拒绝恢复、continued non-tool failure 的 completion routing 对齐，以及跨恢复边界保留 `user_input_required` pause trace。
- `skill_exec` 现在已经有首类 parser/runtime 契约，覆盖 `entrypoint`、`args`、`cwd` 和 `parse_mode`，并通过工具执行层运行脚本资源，同时做路径安全校验，不再退化为模型委托聊天步骤。
- Meta run 现在会把 completed、failed、paused 三类执行结果以最小记录形式持久化进 session 模型，为后续审计和 replay 打下基础，但尚未单独暴露运维界面。
- 现在已经有专用 meta-layer policy gating：通过 `SkillsConfig.MetaSkill.Enabled` 保留元技能安装态，同时从 prompt index 中隐藏、抑制 routing hint，并拒绝显式 `meta_invoke` 调用。

## 验证状态

当前实现已经通过 focused meta-skill 回归和 OpenClaw 整体测试项目验证：

- focused P1 回归切片：`skill_exec` parser/runtime 对齐（`7 passed`）、meta-run 持久化（`4 passed`）、专用 meta policy gating（`3 passed`）
- 完整测试项目：`1907 passed, 0 failed, 0 skipped`

因此，下文描述的是当前已经落地并验证过的 OpenClaw.NET 行为，而不是一个计划中或部分完成的 parity 层。

## 已经对齐的部分

### 1. DAG 编排

OpenClaw.NET 会把元技能的步骤定义为 `composition.steps`，并在执行前校验编排图：

- 重复 step ID
- 缺失依赖
- 自依赖
- 依赖环
- `llm_classify` 的路由目标是否有效

这让元路径具备“先失败快、后执行”的治理能力，而不是默默接受坏图并在执行中后置失败。

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
- `steps[]`，包含状态、耗时、失败代码与继续执行标记

此外，元步骤现在也支持：

- `on_failure` 替代分支，并在 parser/runtime 两层校验
- `retry` 与 `timeout_seconds`，用于工具和模型步骤的有界重试与超时
- `output_contract` / `output_schema`，用于校验 JSON 中间结果的必填字段

这有利于自动化测试、日志排障和运维观测，不需要从自由文本最终回答里反向解析执行状态。

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

## 当前剩余的迁移差距

当前 OpenClaw.NET 的元路径已经覆盖 DAG 执行、fail-fast 校验、显式失败替代、有界 step 执行、结构化执行结果、P0 原生 DSL 兼容层、typed `user_input.clarify`、共享 Jinja 过滤器、`skill_exec` 子进程 entrypoint 执行、最小化的 meta-run 持久化、专用 meta policy gating，以及这一轮 slice 所需的 checkpoint / routing runtime 对齐。但它还不是 OpenSquilla 原生元技能契约的完全等价替代。

| 差距项 | 影响 | 当前状态 |
| --- | --- | --- |
| Meta run history 细节查看、replay、proposals CLI | OpenSquilla 暴露 `skills meta runs ...`、dry-run replay、proposal list/show/accept 等命令，便于审计和运维。 | OpenClaw.NET 现在已经把最小化的 per-run 记录持久化到 session state，并提供了本地 `openclaw skills meta-runs <session-id>` inspection 能力，默认输出 run 摘要，使用 `--verbose` 可展开 per-step trace，支持 `--run <run-id>` 精确筛选，并可通过 `--json` 输出机器可读结果；同时新增了 preview-only 的 `meta-runs replay` 可用性检查，会同时报告最小 replay plan，以及按证据区分的 operator 结论：存在步骤 trace 时为 `auditable_not_replayable`，只有 run 级元数据时为 `metadata_only_not_replayable`；同时也会在 JSON 和文本输出中报告结构化的步骤 readiness、已保留的 artifacts、已保留的步骤摘要，以及带显式原因和类别的结构化缺失 replay 输入，例如 `not_persisted` 与 `not_retained`；已保留的步骤摘要现在也会驱动派生的 replay readiness 标签，例如“失败后继续”的 trace 类型；文本输出里也会展开阻塞 replay requirements 的类别和原因；其中 replay plan 现在直接复用结构化阻塞项，而不是再维护一份纯名字列表；此外，稳定的 replay preview summary、mode、artifact name、requirement kind，以及 readiness/reason 字符串现在也已经集中到共享 session model 常量，而不再散落为 CLI 本地字面量；但仍然缺少可执行 replay 和 proposal 管理工作流。 |
| 更完整的 `skill_exec` stdin / replay 运维能力 | OpenSquilla 的 `skill_exec` 除了子进程执行本身，还覆盖更丰富的 stdin 工作流和周边运维工具。 | OpenClaw.NET 现在已经能以校验过的子进程形式执行 skill entrypoint，但当前切片仍然拒绝 `stdin`，也还没有围绕这些运行结果的 replay 型运维工作流。 |
| 真正的并行 step 调度 | OpenSquilla 可以在 scheduler 限制内并发执行独立 steps。 | OpenClaw.NET 保留了 DAG 顺序正确性，但当前通过运行时循环推进 ready steps，不是并行 scheduler。 |
| 内置 MetaSkill 目录与 creator/proposal 流程 | OpenSquilla 文档包含 `meta-web-research-to-report`、`meta-document-to-decision`、`meta-skill-creator` 等内置工作流，以及 proposal inspection 和 auto-enable audit。 | 当前 OpenClaw.NET 路径聚焦运行时编排；更宽的产品级目录和 proposal 工作流尚未迁移。 |

## 建议

把当前 OpenClaw.NET 的元技能路径理解为已经交付并验证过的强 OpenSquilla 风格实现，覆盖：

- DAG 编排
- 显式失败替代
- 有界 step 执行
- JSON 中间结果校验
- 结构化执行结果
- `output_choices`、`tool_args`、`tool_allowlist`、`clarify`、`when`、route arrays 的原生 DSL 对齐
- `xml_escape`、`slugify`、`truncate`、`tojson` 的共享 Jinja 渲染
- 面向脚本资源、带 parser/runtime 安全校验的 `skill_exec` entrypoint 执行
- session state 中最小化的持久化 meta-run history
- 运行时级别的专用 meta policy gating
- 当前元运行时模型内的 pause/resume checkpoint 安全性与 continued-failure routing 对齐

后续如果要继续追求更完整的 OpenSquilla parity，建议按“仍未补齐且直接影响运行/运维”的顺序推进：

1. **P1：Meta run history 的 replay 与运维能力。** 现在已经有本地 CLI inspection，默认输出 run 摘要，并可通过 `--verbose` 展开 per-step trace，支持 `--run <run-id>` 精确筛选和 `--json` 机器输出，也新增了能区分最小 replay plan、按证据变化的 operator 结论、在 JSON 和文本输出中都可见的结构化步骤 readiness、已保留 artifacts、已保留步骤摘要、基于失败/继续摘要派生的 readiness 标签，以及带显式原因和类别的结构化缺失输入的 preview-only replay 可用性检查；文本输出里也会展开阻塞项的类别和原因；其中 replay plan 会直接复用结构化阻塞项，而不是重复维护纯名字列表；同时，稳定的 replay preview 契约字符串现在来自共享 session model 常量，而不是重复散落在 CLI 本地字面量中；剩下的是可执行 replay 与更完整的运维工作流，而不是只看单次 structured envelope。
2. **P1：`skill_exec` 的 stdin 与运维 ergonomics。** 如果迁移技能依赖 stdin-heavy 工作流，就继续扩展新的子进程路径，并补周边 inspection/replay 能力。
3. **P2：真正的并行 step 调度。** 在保持 DAG 正确性的同时，让独立 steps 并发执行。这能改善性能并贴近 OpenSquilla 行为，但大多数流程不依赖它才能完成迁移。
4. **P2：产品级 catalog、creator 与 proposal 流程。** 只有在 OpenClaw.NET 需要产品级 OpenSquilla parity，而不只是 runtime 可移植性时，再补内置 MetaSkill、`meta-skill-creator`、proposal inspection 与 auto-enable audit。
