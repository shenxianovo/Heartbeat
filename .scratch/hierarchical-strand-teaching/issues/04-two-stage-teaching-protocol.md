# 04: 两阶段统一教学协议

Status: ready-for-agent

## Parent

[PRD](../PRD.md) · [ADR-031](../../../docs/adr/031-hierarchical-strand-episode-teaching-loop.md)

## What to build

用“真实证据卡 → 用户自然语言解释 → LLM 结构化提案 → 用户确认 → 确定性事务提交”替换现有单阶段问题表单，并让主动提问和 Recap 纠正复用同一 KnowledgeChangeSet 写协议。

### Stage 1: evidence

- 从真实 Observation/Segment 时间关系临时形成 ActivityCluster 问题候选，证据卡包含大概时段、跨 Source 活动、可读标题/路径等可核对信息。
- ActivityCluster 是瞬时证据视图，不新增持久化实体，不声称 Segment 已属于某个 Strand/Episode。
- 候选生成继续遵守高精度、限量和 Mute/Probe 规则；已有 StrandMatcher 与 RecurrenceProbe 的命中后果必须区分。
- 问题请求携带足够的证据引用或不可伪造上下文，使第二阶段只能解释用户实际看过的证据，而不是接受任意 Owner/Segment ID。

### Stage 2: proposal

- 接受用户对证据卡的自然语言回答，由 LLM 整理为可编辑 `KnowledgeChangeSet`，覆盖 ADR-031 中的 Create/Update/Move/End Strand、Bind/Mute Matcher、Create/Update/Relate Episode、Create/Resolve Probe、Promote Episode 等操作。
- 提案必须使用现有对象的 UUIDv7、显示 Strand path 和日期以消除同名歧义；不得用名称自动绑定已有 Strand。
- LLM 只返回 proposal，不持有数据库写权限。响应明确区分模型解释、结构化操作、约束警告和无需保存的建议。
- 用户可以逐项编辑、取消或确认操作；未确认项不得提交。
- 调整或版本化旧 `DailyQuestionSet` 缓存/payload，使已有单阶段最终表单不会继续被客户端当作可直接提交的知识写入；部署后旧缓存应安全失效或清空。

### Deterministic commit

- 提供一个共享的事务性 commit endpoint/service，主动提问、Recap 纠正和后续手动复合操作均可复用。
- 服务端重新做 Owner、ID、树、日期、canonical predicate 和并发版本校验，不信任 LLM proposal 或前端编辑结果。
- 一份 change set 中选中的操作全部成功才提交；失败时整批回滚，并返回可定位到具体 operation 的验证错误或 concurrency conflict。
- 对已有实体的修改携带读取时版本；陈旧提案必须返回冲突，不能 last-write-wins 覆盖用户较新的修改。
- commit 响应返回创建/更新后的真实 ID、版本和路径，便于 UI 替换临时 proposal 引用。
- 提案和提交日志遵守现有隐私/遥测边界，不把用户自由文本或私人知识写入非必要日志。

### Tests

- 使用 fake LLM/contract tests 固定 proposal schema；覆盖 malformed/越权/虚构 ID、部分取消、临时 ID 映射、多操作依赖、事务回滚和并发冲突。
- 证明仅生成 proposal 不改变数据库，且两个入口提交同一 change set 得到一致领域效果。

## Acceptance criteria

- [ ] 问题 API 返回来自真实活动的临时 ActivityCluster 证据，而非预填好的最终知识表单
- [ ] 用户自然语言回答可被解释为可编辑 KnowledgeChangeSet，现有对象一律按 UUIDv7 引用
- [ ] LLM 只能生成 proposal，proposal 阶段没有任何知识写入
- [ ] 旧单阶段 DailyQuestionSet payload/cache 被安全版本化或失效，不会绕过最终确认
- [ ] 统一 commit 服务重新验证全部领域不变量、Owner 和并发版本
- [ ] 多操作 change set 原子提交；任一失败时无部分写入
- [ ] 冲突和验证错误能定位具体 operation，成功响应可解析临时 ID 到真实 UUIDv7
- [ ] 主动提问和 Recap 纠正可复用同一 proposal/commit contract
- [ ] contract 与事务测试覆盖恶意或陈旧输入，且日志不泄露不必要的私人文本

## Blocked by

- [01](./01-hierarchical-temporal-strand.md)
- [02](./02-episode-recurrence-probe.md)

## Comments
