# 03: Recap 日期知识投影与惰性判脏

Status: ready-for-agent

## Parent

[PRD](../PRD.md) · [ADR-031](../../../docs/adr/031-hierarchical-strand-episode-teaching-loop.md)

## What to build

让 Recap 在目标本地日期加载有效且相关的 Strand 祖先链与当日 Episode，并用该日期实际使用的 canonical 知识投影判断历史 Recap 是否因知识变化而 stale。

### Date-scoped knowledge assembly

- 只考虑目标日期有效的 Strand；Matcher 命中某节点时，按根到叶顺序注入该节点完整祖先链。
- 父节点被直接命中时不自动激活后代；同一祖先由多个叶命中时只注入一次，并保持确定性顺序。
- 只把 `LocalDate` 等于目标日期的 Episode 注入当日 Recap；有关联 Strand 时带入该 Strand 的有效祖先语境，独立 Episode 也可作为当天事实出现。
- RecurrenceProbe 不进入 Recap prompt；Matcher 仍只是知识检索触发器，不生成 Segment/Episode 归属。
- Digest/Prompt 中保留 Observation 时间线和 Satellite 的叙事判断，不用知识层替换原始活动证据。

### Canonical projection and freshness

- 定义一个稳定、确定性的日期知识投影，至少覆盖实际相关的 Strand ID/祖先结构、Name/Gloss、有效日期、参与命中的 canonical Matcher，以及当日 Episode 的 ID/文本/时间/关联。
- 排序、空值和文本编码必须 canonical；同一逻辑知识在查询顺序不同的情况下产生同一标识，叙事相关字段变化会改变标识。
- Recap 生成成功时保存该日期实际使用的知识投影标识（例如 hash），而不是 Owner 全局知识版本。
- 认证用户读取历史 Recap 时，确定性重算当前日期投影。标识不同则返回“知识已更新，可重新生成”的 stale hint，但读取本身不调用 LLM。
- 旧 Recap 的投影标识为空时惰性视为可重新生成；不批量回填，不在迁移中调用 LLM。
- 用户选择重新生成后写入新投影标识并清除知识 stale；生成失败不得覆盖上次成功正文/投影。
- 保留今天现有的 `SegmentWatermark` 自动刷新策略，并将 Segment freshness 与 knowledge freshness 明确区分。
- 公开只读/分享 Recap 继续纯缓存读取：不读取私有知识、不重算投影、不触发 LLM，也不暴露知识投影细节。

### Tests

- 覆盖日期边界、叶命中祖先链、父命中不展开后代、祖先去重、当日/非当日 Episode、独立 Episode、Probe 排除和 canonical hash 稳定性。
- 覆盖历史读取只提示不生成、旧 null hash、目标日手动重生成、今日 SegmentWatermark 共存及 public cache-only 路径。

## Acceptance criteria

- [ ] 目标日期仅加载当日有效且被证据命中的 Strand，叶节点命中会按根到叶带入完整祖先链
- [ ] 父节点命中不激活后代，重复祖先不会重复注入
- [ ] 当日 Episode 可进入 Recap；非当日 Episode 和所有 RecurrenceProbe 均不进入
- [ ] Recap 保存实际使用的日期级 canonical 知识投影标识，不使用全局知识版本
- [ ] 相关知识变化会使历史认证读取返回 stale hint，读取本身不调用 LLM
- [ ] 无投影标识的旧 Recap 惰性提示可重新生成，不批量回填
- [ ] 今日 SegmentWatermark 自动刷新行为保持，并能与知识 stale 独立判断
- [ ] public/share 读取保持纯缓存路径，不访问私有知识或触发生成
- [ ] 自动化测试证明投影排序稳定且叙事相关变化能够被检测

## Blocked by

- [01](./01-hierarchical-temporal-strand.md)
- [02](./02-episode-recurrence-probe.md)

## Comments
