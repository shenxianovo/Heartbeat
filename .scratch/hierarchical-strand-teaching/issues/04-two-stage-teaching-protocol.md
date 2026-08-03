# 04: 两阶段统一教学协议

Status: done

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

- [x] 问题 API 返回来自真实活动的临时 ActivityCluster 证据，而非预填好的最终知识表单
- [x] 用户自然语言回答可被解释为可编辑 KnowledgeChangeSet，现有对象一律按 UUIDv7 引用
- [x] LLM 只能生成 proposal，proposal 阶段没有任何知识写入
- [x] 旧单阶段 DailyQuestionSet payload/cache 被安全版本化或失效，不会绕过最终确认
- [x] 统一 commit 服务重新验证全部领域不变量、Owner 和并发版本
- [x] 多操作 change set 原子提交；任一失败时无部分写入
- [x] 冲突和验证错误能定位具体 operation，成功响应可解析临时 ID 到真实 UUIDv7
- [x] 主动提问和 Recap 纠正可复用同一 proposal/commit contract
- [x] contract 与事务测试覆盖恶意或陈旧输入，且日志不泄露不必要的私人文本

## Blocked by

- [01](./01-hierarchical-temporal-strand.md)
- [02](./02-episode-recurrence-probe.md)

## Comments

- 2026-08-03 agent: 实现落地。要点——
  - **Stage 1（证据卡）**：判官（`IAskingGenerator`）输出收窄为 `AskingCandidate(Question, Matcher)`——
    名字/释义预填提案退役，prompt 明确"只提问，不替用户猜答案"。证据由新纯函数
    `ActivityClusterEvidence.Materialize` 从真实 segments 物化：谓词命中段决定大概时段，
    时段内跨 Source 观察按 (Source, 最浅读数) 聚合（带主要 Detail、时长、是否命中指纹），
    与 Recap 投影同一窗口/深度表规则。判官提名在当日证据上零命中的候选整个丢弃（防造值）。
    问题 Id = 生成时 UUIDv7，第二阶段只凭它取回服务端自己发出的证据。
  - **RecurrenceProbe 命中读时确定性追加**（零 LLM、不占判官配额、不进缓存）：kind=recurrence、
    Id 恒等于 ProbeId、携带源 Episode 文本/日期帮回忆；与活跃 Probe 同谓词的 cluster 问题让位。
    与 StrandMatcher 命中后果由 kind 区分。
  - **缓存版本化**：`DailyQuestionSet.PayloadVersion`（当前 = 2），版本不符视为未命中重生成；
    `TwoStageQuestionPayload` 迁移加列并 DELETE 旧行——旧单阶段表单双保险失效，不迁移机器提案。
  - **Stage 2（proposal，零写入）**：`POST questions/{id}/propose`（date + 自然语言 answer）。
    `KnowledgeProposalService` 先按 (Owner, 日窗口, 问题 Id) 取证（伪造/跨 Owner/已裁决 → 404），
    再装配知识语境快照（全部 Strand 带 path/日期/读取时版本 + 目标日 Episode ∪ recurrence 源
    Episode + 活跃 Probe），`IProposalGenerator`（fake 可注入）整理，`ProposalSanitizer`（纯函数）
    消毒：虚构/越权 UUID 整条剔除出 warning、版本由服务端语境盖章（不信 LLM 回显）、OpId 只能
    向后引用同类型 create、update 语义省略字段回填现状、Matcher 同一 canonicalization。
    响应四分：explanation / operations / warnings / suggestions。
  - **Commit（共享事务端）**：`POST changesets`（`CommitChangeSetRequest`，12 种 op 覆盖 ADR-031 §6
    全表）。`KnowledgeCommitService`：set 级形状校验（空集/OpId 重复/前向引用/缺版本）在事务前；
    单事务内逐 op 委托 KnowledgeService/EpisodeService 重校验（Owner/树/日期/同名重叠/并发版本），
    EpisodeService.PromoteEpisodeAsync 改为复用外层事务（CurrentTransaction 判空）。任一失败整批
    回滚，`ChangeSetErrorResponse.FailedOpId` 定位；成功回读真实 UUIDv7/版本/path。bindMatcher 是
    追加语义（canonical 已存在收敛），区别于 updateStrand 的整组替换。
  - **隐私**：全链路无新增日志语句——用户自由文本只进 LLM 请求与知识表本体。
  - 测试 +32（全套 236 过）：QuestionServiceTests 重写（证据物化/零命中丢弃/旧版本缓存失效/
    recurrence 生命周期/取证纪律）、ProposalSanitizerTests（schema 宽容解析、虚构/越权/promoted
    越权、OpId 纪律、版本盖章）、KnowledgeProposalServiceTests（零写入五表快照、Owner 隔离、
    fake LLM、双入口同 change set 同领域效果）、KnowledgeCommitServiceTests（临时 ID 链、中途
    失败回滚、不变量重校验、陈旧版本冲突不覆盖新编辑、部分取消、Episode→Probe→提升链）。
  - 注意：前端 `client.ts`（NSwag 生成物）与 `StrandQuestions.vue` 仍引用旧 `QuestionItemResponse`
    契约，属 issue 05（两阶段前端）范畴，届时从 live OpenAPI 重新生成。
