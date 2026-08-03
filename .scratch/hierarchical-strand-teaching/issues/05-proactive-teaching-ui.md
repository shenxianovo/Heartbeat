# 05: 主动教学两阶段 UI

Status: done

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

- [x] 主动问题先展示真实活动簇的时段和跨 Source 证据，再接受自然语言解释
- [x] 提案生成前后均不会自动写入知识，只有最终确认会提交
- [x] 用户可逐项编辑或取消 Strand、Matcher、Episode 和 Probe 操作
- [x] 已有 Strand 选择显示 path/date 并按 UUIDv7 提交，同名不会错误绑定
- [x] UI 能表达一次性、持续、暂不确定、跳过和静音五类结果
- [x] 成功、验证错误和并发冲突都有准确反馈，失败不会丢失可恢复编辑
- [x] Episode 提升明确保留原记录，不呈现为类型转换
- [x] 前端测试覆盖主路径及主要失败/消歧分支

## Blocked by

- [04](./04-two-stage-teaching-protocol.md)

## Comments

- 2026-08-03 agent: 实现落地。要点——
  - **client 重生成**：起临时 Postgres + Development 后端导出 live `/openapi/v1.json`，
    NSwag 14.7.1 重生成 `frontend/src/api/client.ts`——旧 `IQuestionItemResponse` /
    `DailyQuestionsResponse` 契约随之消失，两阶段类型（AskingQuestions / Proposal /
    ChangeSet 全家）进入生成物。
  - **api/index.ts**：`fetchDailyQuestions` 改回 `AskingQuestionsResponse`；新增
    `proposeFromQuestion`（date 手拼保时区 offset，与 questions 同约）与
    `commitChangeSet`（body 走 `CommitChangeSetRequest.fromJS(...).toJSON` 序列化——
    DateOnly 字段必须输出 `yyyy-MM-dd` 本地分量，裸 `JSON.stringify` 会把 Date 变 UTC
    datetime）。失败响应解析回 `KnowledgeErrorResponse` / `ChangeSetErrorResponse` 塞进
    `ApiException.result`，`knowledgeErrorOf` / `changeSetErrorOf` 取回结构化错误。
  - **`src/teaching/teachingFlow.ts`（新，纯逻辑层）**：review 状态（默认全启用、
    selectedOps 保持提案顺序——OpId 只能向后引用）；分区渲染（脉络/指纹/片段事实/探针）；
    `precheck`（取消被依赖新建项 → blockedOpIds 挡提交 + 警告；空名字/文本警告；
    服务端仍是最终裁判）；Strand 引用选项（空值 → 前面的新建项 op:opId → 已有节点
    id:uuid，label = 完整 path + `起 ~ 止`日期，同名不同时期可区分）；
    `rebindMatcherTarget`（bindMatcher 换绑时 expectedVersion 跟着目标走：已有节点盖
    读取时版本、OpId 引用清空）；`interpretProposeError`（404 = 证据过期只能刷新，
    502/网络 = 回答保留可重试）与 `interpretCommitError`（409 冲突码 → conflict 出口，
    failedOpId 定位，验证失败人话词典）；`commitSummary`（真实 path/version 回读，
    提升明说"原片段事实已保留"）。
  - **StrandQuestions.vue 重写**：每卡状态机 evidence → proposing → review →
    committing → done。证据卡列出时段 + 跨 Source 观察行（指纹命中行高亮、旁证淡化、
    时长右对齐），明示"系统观察到的活动——归属由你决定"；recurrence 卡带源 Episode
    原文/日期帮回忆。自然语言 textarea 支持五类意图自由表达;"整理成变更"期间禁输入、
    明示零写入。review 逐项 checkbox 启用 + 内联编辑（名字/Gloss/日期/父级/关联
    select）；moveStrand 显示"移动会改写历史解释,现实归属变化应结束旧节点新建"警示；
    muteMatcher/resolveProbe 明示不隐藏原始 Observation。提交失败回 review 保留全部
    编辑并标红 failedOpId 操作；409 出"重新加载最新知识并重新审阅"（重新 propose,
    版本重新盖章）,不静默覆盖。静音需二次确认:cluster 走 `POST mutes`,recurrence 走
    changesets 的 `resolveProbe(muted)`（静音指纹停不掉活跃 Probe）。跳过纯客户端。
  - **测试 +36**（`teachingFlow.test.ts`，全套 97 过；`vue-tsc -b` 与 `vite build` 干净）：
    选中/取消、分区、OpId 依赖预检（含 promoteEpisode 双引用、连带取消、恢复解除）、
    同名 Strand path/date 消歧、id/op/空引用往返、换绑版本盖章、日期桥、
    propose 404/502/网络分支、commit 冲突/验证/set 级/传输层分支、回读摘要。
  - 组件测试基建（@vue/test-utils/jsdom）本仓库没有,沿用"逻辑抽纯模块 + .vue 保持薄"
    的既有惯例,evidence → answer → proposal → review → commit 的可判定规则全部下沉
    teachingFlow 后逐分支测试。
