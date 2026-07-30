# Hierarchical Strand Teaching

Status: ready-for-agent

## Parent decision

[ADR-031](../../docs/adr/031-hierarchical-strand-episode-teaching-loop.md)

## Problem

Recap 已能观察用户打开了什么，但扁平 Strand 无法表达“哔哩哔哩实习 → Hyperframes →
产品调研与可行性分析”，也没有稳定承载“今天具体发生了什么”的用户事实。第一次出现的
活动还无法预知它是一次性 Episode 还是持续 Strand。主动发问、Recap 纠正与手动管理目前
也没有一个共享的教学协议。

## Goal

让 Heartbeat 以用户确认的两种知识改善 Recap：

- **Strand**：跨日期延续、严格单父级、带近似有效日期的私人语境；
- **Episode**：有大概时间边界的一次具体发生，可选关联一个最具体 Strand。

模型从 Observation 形成的 ActivityCluster 只作证据；机器先提议，用户确认后才写入。
三种教学入口共享同一事务性变更模型，历史 Recap 按相关知识投影惰性判脏。

## Product flow

### Proactive teaching

```text
当日 Observation → ActivityCluster 证据卡
→ 用户自然语言回答
→ LLM 整理结构化 KnowledgeChangeSet
→ 用户逐项编辑/确认
→ 事务提交
```

一次确认可同时创建/更新 Strand、Matcher、Episode 与 RecurrenceProbe。明确一次性的行为只
创建 Episode；尚不确定是否持续时可创建 Episode + Probe；明确持续时创建/绑定 Strand，
并可同时保存当天 Episode。

### Recap correction

```text
用户从某日 Recap 发起纠正
→ 同一解释/确认协议
→ 写入持续 Strand 知识和/或当天 Episode
→ 立即强制重生成该日 Recap
```

不直接编辑 Recap 正文。其他受影响日期只在读取时提示可重新生成。

### Manual management

用户可浏览和编辑 Strand 树、起止日期、Gloss、Matcher、Episode 关联与 Probe，并可从
Episode 发起非破坏性提升。已有 Strand 的所有选择和修改都按 UUIDv7 ID，而非名称。

## Domain invariants

### Strand

- 应用层生成 UUIDv7；现有 Strand 保留 ID，其他知识实体迁移到统一 UUIDv7 身份。
- 严格单父级、无环、任意实用深度、无固定 Kind。
- 父节点允许零 Matcher。
- `StartedOn?` / `EndedOn?` 精确到用户本地日期，未知端点视为无界。
- 同 Owner、同父、同 NormalizedName 的有效日期范围不得重叠。
- 子级已知日期范围不得超出父级已知范围。
- 子命中带入完整祖先链；父命中不激活后代。
- `MoveStrand` 是纠错；真实语境迁移通过结束旧节点并创建新节点表达。

### Matcher

- 是知识检索触发器，不是 Segment 分类或工时归属。
- 命中只在 Strand 对目标日期有效时生效。
- 高精度优先；Satellite 不为召回进入指纹。
- 不新增 `Segment → Strand` 持久化关系。

### Episode

- 只由用户确认创建；ActivityCluster 或 Matcher 命中不能自动创建。
- 有 `LocalDate`、可选近似起止时间、自由文本和可空单一 `RelatedStrandId`。
- 不进入 Strand 树，不拥有 Segment，不做多对多。
- 提升时保留 Episode，新增/绑定 Strand 后再设置关联。

### RecurrenceProbe

- 只用于未确定是否持续的 Episode。
- 使用 Matcher 同形的 canonical 路径谓词，但命中只触发 Asking。
- 不注入 Recap、不自动提升、不自动关联历史 Episode。
- 提升、否认或静音后必须解决，避免重复发问。

### Confirmation

- LLM 只产提案，不写数据库。
- 私有语义必须经用户最终确认。
- 一份 KnowledgeChangeSet 中选中的操作事务性提交；任何约束失败整批回滚。
- 更新已有对象携带并发版本，过期提案返回冲突而非覆盖新编辑。

## Recap freshness

- 继续保留今天的 SegmentWatermark 自动刷新策略。
- 生成时保存该日期相关 Strand 祖先链、Matcher、有效日期和 Episode 的 canonical 知识投影哈希。
- 历史读取时确定性重算；不同只返回 stale hint，不自动调用 LLM。
- 旧 Recap 没有知识投影哈希时惰性视为待重新生成，不做批量迁移或生成。
- 公开只读 Recap 保持纯缓存读取，不触发私有知识重算或 LLM。

## Migration

- 现有 Strand 全部迁移为顶层节点，日期未知，保留名称、Gloss、UUID 和 Matcher。
- StrandMatcher/MutedMatcher 等知识实体若仍为自增 ID，迁移为应用层 UUIDv7；谓词的业务身份
  仍由 `(Source, canonical StepsJson)` 决定。
- 旧按 `(OwnerId, lower(Name))` 归入的服务语义退役；新建/绑定已有节点必须显式父级或 ID。
- DailyQuestionSet 的旧问题卡 payload 与两阶段协议不兼容，可以清空缓存，不迁移机器提案。
- Recap 新增知识投影标识；旧行不批量重生成。

## Non-goals

- 多父级 DAG；
- 固定 Strand Kind；
- 精确工时、项目报表或 Segment 归属；
- 持久化模型推导的 ActivityCluster；
- 自动创建 Episode、自动提升 Strand、自动关联历史 Episode；
- 时间版本化父子边；
- 批量重生成历史 Recap；
- 从 Recap 散文自动生成问题。

## Delivery slices

1. `01-hierarchical-temporal-strand.md` — Strand 树、日期和确定性约束；
2. `02-episode-recurrence-probe.md` — Episode、Probe 与提升服务；
3. `03-recap-knowledge-projection.md` — 日期知识投影、注入和惰性 staleness；
4. `04-two-stage-teaching-protocol.md` — ActivityCluster 问题与结构化教学协议；
5. `05-proactive-teaching-ui.md` — 主动提问的两阶段前端；
6. `06-recap-correction-flow.md` — Recap 纠正与目标日立即重生成；
7. `07-manual-knowledge-manager.md` — Strand/Episode/Probe 手动管理。
