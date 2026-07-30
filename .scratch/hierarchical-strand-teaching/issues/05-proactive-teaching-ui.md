# 05: 主动教学两阶段 UI

Status: ready-for-agent

## Parent

[PRD](../PRD.md) · [ADR-031](../../../docs/adr/031-hierarchical-strand-episode-teaching-loop.md)

## What to build

把现有主动问题 UI 改造成两阶段教学体验：先让用户核对真实活动簇并自然语言解释，再审阅模型整理的结构化知识变更，最后显式确认提交。

### Evidence and answer

- 证据卡展示大概时段以及 VS Code、Browser、ChatGPT 等跨 Source 证据；明确它是“系统观察到的活动”，不是已经确定的项目归属。
- 提供自然语言回答入口，允许用户表达：一次性 Episode、持续 Strand、已有父子语境、暂不确定并创建 Probe，或这不是有意义的模式。
- 保留跳过和静音能力；跳过不写私人事实，静音按确认后的 Mute 语义提交，不隐藏原始 Observation。
- 处理提案生成的 loading、失败、重试和证据已过期状态，不能在等待 LLM 时提前写入知识。

### Proposal review

- 把 `KnowledgeChangeSet` 渲染成可逐项启用、编辑和删除的 review：Strand 树变更、日期、Gloss、Matcher、Episode、关联与 Probe 分区清晰。
- 选择已有 Strand 时按 ID 提交，并显示完整 path、有效日期和必要的 Gloss；同名不同时期必须可区分。
- 清楚标示“移动会改写历史解释”与“结束旧节点并新建表示现实归属变化”的差别。
- 对 Episode 明确显示目标 `LocalDate`、近似时间和最多一个 RelatedStrand；提升操作说明原 Episode 会保留。
- 在提交前展示服务端可预知的约束警告；最终提交仍以服务端校验为准。

### Commit feedback

- 只有用户点击最终确认才调用 commit。
- 成功后用返回的真实 UUID/版本更新界面，并从待回答列表移除或标记已教学。
- 验证失败保留用户编辑内容并定位到对应 operation；并发冲突提供重新加载最新知识并重新审阅的路径，不能静默覆盖。
- 提交失败不得显示为已保存，也不得丢失用户原始回答和仍可修复的 proposal。

### Tests

- 覆盖 evidence → answer → proposal → review → commit 的主路径，以及跳过、静音、提案失败、验证失败和并发冲突。
- 测试同名 Strand 的 path/date 消歧、逐项取消、Episode + Strand 同次提交和 Probe 分支。

## Acceptance criteria

- [ ] 主动问题先展示真实活动簇的时段和跨 Source 证据，再接受自然语言解释
- [ ] 提案生成前后均不会自动写入知识，只有最终确认会提交
- [ ] 用户可逐项编辑或取消 Strand、Matcher、Episode 和 Probe 操作
- [ ] 已有 Strand 选择显示 path/date 并按 UUIDv7 提交，同名不会错误绑定
- [ ] UI 能表达一次性、持续、暂不确定、跳过和静音五类结果
- [ ] 成功、验证错误和并发冲突都有准确反馈，失败不会丢失可恢复编辑
- [ ] Episode 提升明确保留原记录，不呈现为类型转换
- [ ] 前端测试覆盖主路径及主要失败/消歧分支

## Blocked by

- [04](./04-two-stage-teaching-protocol.md)

## Comments
