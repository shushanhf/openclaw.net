# MetaSkill 用户指南 (zh-CN)

> 中文用户指南。英文版：[`meta-skill-user-guide.md`](../meta-skill-user-guide.md)

MetaSkill 将重复的多步 AI 协作模式转化为可复用、可触发、可审计、可改进的任务协议。

一次普通对话解决一个请求。一个 MetaSkill 则保存了一种完成高价值工作的方式——
带 `depends_on` DAG 执行、`on_failure` 降级分支、`user_input` 暂停点和完整审计轨迹。

## 两种激活方式

### 自然委托

直接描述结果，OpenClaw.NET 通过触发词和优先级匹配最佳 MetaSkill：

```
生成一份我最近 7 天 commit 的周报。
```

### 显式委托

直接指定 MetaSkill 名称，适合重要或易混淆的任务：

```
使用 meta-skill `weekly-report`。

生成最近 7 天的团队周报，包含成员贡献、关键合并和阻塞项。
```

## 检查运行历史

```sh
openclaw skills meta-runs <session-id>
openclaw skills meta-runs <session-id> --run <run-id> --verbose
openclaw skills meta-runs <session-id> --json
```

回放预览（只读，不执行）：

```sh
openclaw skills meta-runs replay <session-id> --run <run-id>
```

审计重建：

```sh
openclaw skills meta-runs reconstruct <session-id> --run <run-id>
```

## 提案管理

MetaSkill 创建流程会先写入提案，审查后再安装：

```sh
openclaw skills meta-runs proposals                          # 列出候选提案
openclaw skills meta-runs proposals show <sid> --proposal <id> # 查看详情
openclaw skills meta-runs proposals accept <sid> --proposal <id> # 接受
```

## 发现 MetaSkill

```sh
openclaw skills catalog --kind meta          # 列出所有模板
openclaw skills inspect <skill-name>         # 查看组合结构
```

## 安全说明

MetaSkill 输出是可审查的工作产品和决策支持草稿。它们**不是**法律、医疗、
金融、招聘、学术、安全等高风险场景的最终专业建议。

发布、应用、安装、付款、签名、发送消息或修改生产系统等操作需要用户明确授权，
且始终是用户的责任。

当 MetaSkill 依赖搜索、文档解析、LLM 判断或第三方工具时，结果可能受源质量、
模型限制、工具可用性、上下文完整性和时效性变化的影响。用户应审查事实、引用、
假设、风险和不可验证的声明，尤其是在高风险场景中。

---

[站点地图](../SITE_MAP.md) · [入门指南](../GETTING_STARTED.md) · [编写指南](../authoring/meta-skills.md)

发布、安装、付款、签名、发送消息或修改生产系统等操作需要用户明确授权。
