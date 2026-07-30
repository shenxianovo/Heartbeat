# 02: Episode 与 RecurrenceProbe

Status: ready-for-agent

## Parent

[PRD](../PRD.md) · [ADR-031](../../../docs/adr/031-hierarchical-strand-episode-teaching-loop.md)

## What to build

增加用户确认的有界事实 Episode，以及用于“尚不确定是否会持续”的 RecurrenceProbe。两者都属于知识层，但不能把临时推断或 Matcher 命中静默落库。

### Episode

- Episode 使用应用层生成的 UUIDv7，至少包含 Owner、`LocalDate`、自由文本、可空近似起止时间、可空 `RelatedStrandId`、审计时间和并发版本。
- `RelatedStrandId` 最多关联一个最具体 Strand，必须属于同一 Owner；Episode 不进入 Strand 树、不拥有 Segment、不建立多对多 Strand 关系。
- 支持创建、读取、编辑、删除或归档（按仓库既有删除约定）、关联/解除 Strand；按日期和 Strand 浏览时保持 Owner 隔离。
- 近似时间仅服务叙事。若起止均提供，验证顺序及与 `LocalDate` 的一致解释，但不要把它升级为精确工时模型。
- Episode 只接受用户确认后的确定性写请求；ActivityCluster、Observation 或 StrandMatcher 命中均不得直接调用自动创建路径。

### RecurrenceProbe

- Probe 使用 UUIDv7，关联一个 Episode，并保存与 Matcher 同形、同 canonicalization 规则的路径谓词。
- Probe 的身份和生命周期独立于 StrandMatcher：命中只产生 Asking 候选，不向 Recap 注入旧 Episode，也不创建或绑定 Strand。
- 支持活跃、已提升、已否认、已静音等可解释的解决结果（可用状态加 resolution 表达）；任何解决结果都不再重复发问。
- 同一未解决 Episode/predicate 不得产生重复活跃 Probe；跨 Owner 引用必须拒绝。

### Promotion transaction

- 提供非破坏性的提升服务：保留 Episode，新建或选择一个 Strand，按用户选择关联 Episode，并可将 Probe 谓词提议为该 Strand 的 Matcher。
- 提升必须是明确确认后的事务：任何 Strand 约束、并发版本、Matcher canonical 冲突或关联失败都整批回滚。
- 提升不得自动关联其他历史 Episode，不得把 Episode 行转换或删除，也不得因为 Probe 再次命中自行执行。

### Tests

- 覆盖 Episode 单 Strand 关联、跨 Owner 拒绝、时间边界、Probe 去重与 lifecycle、提升成功和事务回滚。
- 加入负向测试，证明 Matcher/Probe 命中本身不会创建 Episode、Strand 或历史关联。

## Acceptance criteria

- [ ] Episode 以 UUIDv7 持久化，支持本地日期、近似时间、文本和可空单一 RelatedStrandId
- [ ] Episode 不拥有 Segment、不进入 Strand 树，且不能关联多个 Strand 或其他 Owner 的 Strand
- [ ] Episode 的创建只能通过显式、已确认的写路径完成
- [ ] Probe 复用 Matcher 的 canonical 路径谓词，但不复用其 Recap 注入后果
- [ ] 活跃 Probe 可被提升、否认或静音；解决后不再产生重复问题
- [ ] 同一 Episode 和 canonical predicate 不会存在重复活跃 Probe
- [ ] 提升保留原 Episode，并在一个事务中完成 Strand 选择/创建、可选 Matcher 绑定、Episode 关联和 Probe 解决
- [ ] 提升失败整批回滚，且不会自动关联其他 Episode
- [ ] 自动化测试覆盖领域约束、Owner 隔离、lifecycle 与负向自动化边界

## Blocked by

- [01](./01-hierarchical-temporal-strand.md)

## Comments
