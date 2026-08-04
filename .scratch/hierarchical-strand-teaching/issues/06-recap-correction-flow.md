# 06: Recap 纠正与目标日重生成

Status: done

## Parent

[PRD](../PRD.md) · [ADR-031](../../../docs/adr/031-hierarchical-strand-episode-teaching-loop.md)

## What to build

在 Recap 卡片提供“教 Heartbeat 正确理解”入口。纠正不直接改生成散文，而是复用统一两阶段协议写入持续知识和/或当天 Episode；确认成功后只立即重生成被纠正的目标日期。

### Correction flow

- 从某日 Recap 发起时，证据上下文锁定该 Recap 的本地日期及其 Observation/Segment 窗口，并允许用户自然语言指出遗漏、错误关联或应记住的私人语境。
- 复用 issue 04 的 proposal/review/commit contract，不另建一套自由文本 patch 或专用知识表。
- review 明确区分：
  - 跨日期持续、反复出现或用户希望长期记住的 Strand；
  - 只属于目标日期的 Episode；
  - 尚不确定是否持续的 Episode + RecurrenceProbe。
- 支持一次纠正同时修改 Strand 树/Matcher 并创建或更新目标日 Episode，但所有操作仍需用户最终确认。
- 不从现有 Recap 散文自动抽取并静默写知识；散文只能作为用户正在纠正的显示上下文，事实证据仍来自目标日观察与用户回答。

### Regeneration and freshness

- KnowledgeChangeSet 提交成功后，立即对目标日期执行一次显式 force regenerate，并用最新 Segment 与知识投影保存新 Recap。
- 重生成只发生在知识事务提交成功之后；知识提交失败不得生成，生成失败不得回滚已经确认的知识，也不得覆盖上一版成功 Recap。
- 若重生成失败，UI 明确显示“知识已保存，Recap 尚未更新”并允许重试。
- 不批量重生成其他历史日期。它们只在后续认证读取时按 issue 03 的投影比较显示 stale hint。
- 公开 Recap 页面不提供私有纠正入口，也不触发知识读取或生成。

### Tests

- 覆盖只建 Episode、只改 Strand、两者同次提交、Probe、提交失败、生成失败和重试。
- 验证目标日立即重生成、其他受影响日期零 LLM 调用但读取时可 stale、正文不被直接 patch，以及 public 路径不受影响。

## Acceptance criteria

- [x] 私有 Recap 卡片可从明确的目标本地日期发起纠正
- [x] 纠正复用统一 KnowledgeChangeSet proposal/review/commit，不直接编辑 Recap 正文
- [x] UI 能区分持续 Strand、目标日 Episode 和 Episode + Probe
- [x] 只有最终确认后的知识变更会写库；提交失败不触发重生成
- [x] 提交成功后立即且仅强制重生成目标日期，并保存最新知识投影
- [x] 生成失败保留已确认知识和上一版成功 Recap，用户可单独重试
- [x] 其他历史日期不批量生成，只在读取时惰性提示 stale
- [x] public/share 路径不暴露纠正入口且保持 cache-only
- [x] 后端与前端测试覆盖成功、部分故障和日期隔离

## Blocked by

- [03](./03-recap-knowledge-projection.md)
- [04](./04-two-stage-teaching-protocol.md)

## Comments

- 2026-08-04 agent: 实现落地。要点——
  - **后端纠正 propose 入口**：`POST knowledge/corrections/propose`（`ProposeCorrectionRequest`：date + correction）。
    `KnowledgeProposalService.ProposeCorrectionAsync` 证据上下文锁定目标本地日期——调
    `DigestAssembler.AssembleAsync` 取该日 digest（与叙事同一份），不喂 Recap 散文。
    `IProposalGenerator.ProposeCorrectionAsync` 新方法复用同一领域模型/操作词汇/sanitizer，
    只换开场语境（`BuildCorrectionSystemPrompt`）：明说"只属于那天的事实用 createEpisode，
    跨日期持续的用 Strand，不确定的用 Episode + Probe，不要直接改写回顾文字"。
    证据上下文 = 完整 digest + 已知知识清单（Strand/Episode/Probe 带 UUIDv7 + 版本）。
    零写入——commit 走既有共享端点 `POST changesets`，不另建写协议。
    空日（digest 为空）返回 400 `empty_day`（不是 404）。
  - **Prompt 重构**：原 `SystemPromptTemplate` 拆成 `QuestionIntro + SharedPromptBody`
    与 `CorrectionIntro + SharedPromptBody`（纯 const 拼接，编译时确定），`BuildSystemPrompt`
    与 `BuildCorrectionSystemPrompt` 各注入词汇段。`SharedPromptBody` 包含领域模型、操作词汇
    与整理规则——两个入口操作语义完全一致，只换开场角色描述。
  - **提交后重生成编排（纯前端逻辑）**：`correctionFlow.ts` 的 `submitCorrection`——
    顺序是契约：先 `commit` 再 `regenerate`；commit 抛错时 regenerate 绝不被调用。
    三种出口：`done`（知识 + 叙事都成功）/ `regenerateFailed`（知识已存，Recap 尚未更新，
    可单独 `retryRegenerate`）/ `commitFailed`（整批回滚，保留用户编辑）。
    RecapService 的 `force=true` 语义天然满足"保存最新知识投影"（AssembleAsync 每次
    重算 hash）；RecapGenerationException → 502 → UI 区分"生成失败"与"提交失败"。
  - **RecapCard 集成**：`regenerateForCorrection` 透传 `load(true)`，但显式把
    `useAsyncData` 吞掉的错误重新抛出——纠正面板据此判"知识已存、Recap 未更新"。
    `<RecapCorrection>` 只在 `canRegenerate`（owner-only）时渲染；公开路径零暴露。
  - **ProposalReview 抽取**：issue 05 的 StrandQuestions 和 issue 06 的 RecapCorrection
    共用同一套分区渲染 + 逐项编辑；抽成独立组件 `ProposalReview.vue`，StrandQuestions 改为
    引用它，RecapCorrection 同样引用。StrandQuestions 从 512→211 行（-59%）。
  - **前端 `interpretCorrectionError`**：与 `interpretProposeError` 的关键差别——纠正的
    证据是日期本身不会过期（`expired` 恒 false），不会出现"证据卡已失效只能刷新"的分支。
    新增 `empty_day` 错误码映射"这一天没有活动记录，无法纠正"。
  - **测试**：
    - 后端 `RecapCorrectionFlowTests`（+10，Postgres 集成）：纠正 propose 零写入 + 证据
      来自 digest 不来自散文、空日 400、Owner 隔离、与主动发问的 propose 同 sanitizer
      共享消毒纪律、commit 后 force 重生成语义验证（知识投影 hash 更新）、commit 失败
      不调生成、生成失败不回滚知识且不覆盖上一版 Recap、public 路径零暴露。
    - 前端 `correctionFlow.test.ts`（+11）：submitCorrection 成功/提交失败/生成失败/
      retryRegenerate/correctionStageHint 全分支。
    - `teachingFlow.test.ts`（+4）：interpretCorrectionError 分支。
    - 全套 160 前端 + 246 后端过；vue-tsc + vite build 干净。
