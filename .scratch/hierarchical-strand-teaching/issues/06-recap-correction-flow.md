# 06: Recap 纠正与目标日重生成

Status: ready-for-agent

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

- [ ] 私有 Recap 卡片可从明确的目标本地日期发起纠正
- [ ] 纠正复用统一 KnowledgeChangeSet proposal/review/commit，不直接编辑 Recap 正文
- [ ] UI 能区分持续 Strand、目标日 Episode 和 Episode + Probe
- [ ] 只有最终确认后的知识变更会写库；提交失败不触发重生成
- [ ] 提交成功后立即且仅强制重生成目标日期，并保存最新知识投影
- [ ] 生成失败保留已确认知识和上一版成功 Recap，用户可单独重试
- [ ] 其他历史日期不批量生成，只在读取时惰性提示 stale
- [ ] public/share 路径不暴露纠正入口且保持 cache-only
- [ ] 后端与前端测试覆盖成功、部分故障和日期隔离

## Blocked by

- [03](./03-recap-knowledge-projection.md)
- [04](./04-two-stage-teaching-protocol.md)

## Comments
