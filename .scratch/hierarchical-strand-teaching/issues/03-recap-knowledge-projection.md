# 03: Recap 日期知识投影与惰性判脏

Status: done

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

- [x] 目标日期仅加载当日有效且被证据命中的 Strand，叶节点命中会按根到叶带入完整祖先链
- [x] 父节点命中不激活后代，重复祖先不会重复注入
- [x] 当日 Episode 可进入 Recap；非当日 Episode 和所有 RecurrenceProbe 均不进入
- [x] Recap 保存实际使用的日期级 canonical 知识投影标识，不使用全局知识版本
- [x] 相关知识变化会使历史认证读取返回 stale hint，读取本身不调用 LLM
- [x] 无投影标识的旧 Recap 惰性提示可重新生成，不批量回填
- [x] 今日 SegmentWatermark 自动刷新行为保持，并能与知识 stale 独立判断
- [x] public/share 读取保持纯缓存路径，不访问私有知识或触发生成
- [x] 自动化测试证明投影排序稳定且叙事相关变化能够被检测

## Blocked by

- [01](./01-hierarchical-temporal-strand.md)
- [02](./02-episode-recurrence-probe.md)

## Comments

- 2026-08-03 agent: 实现落地。要点——
  - 新纯函数层 `KnowledgeProjection.Resolve(date, strands, episodes, observations)`：日期有效性
    （未知端点无界，与 KnowledgeService 重叠规则同尺）→ Matcher 命中 → 祖先链注入（按 Id 去重、
    path 排序确定）→ 当日 Episode（关联 Strand 有效才带语境，失效降为独立事实）→ canonical hash。
  - hash = SHA-256(STJ 序列化的 canonical 投影)：Strand 按 Id 排序（含结构/名字/释义/日期）、
    命中 Matcher 按 (StrandId, Source, StepsJson) 排序、Episode 按 Id 排序、时间统一 UTC "O" 格式——
    查询顺序无关；只覆盖**实际注入**的知识，无关知识变化不判脏。
  - `RecapProjection.ResolveKnowledge` 是生成与判脏共用的单一入口（同一窗口规则、同一 DepthTables
    读数提取），保证重算与生成时字节一致。digest 渲染换成 path 形式（"实习 → Hyperframes：gloss"）
    加"当天事实"块；`KnownStrandInput`（ADR-029 平面注入）退役。
  - `Recap.KnowledgeHash` 可空列纯增迁移；旧行 null → 认证读取恒 stale hint，不回填不调 LLM。
    失败不覆盖上次成功正文/投影（原 fail-no-cache 路径天然满足，测试锁定）。
  - `RecapService`：缓存命中时经 `DigestAssembler.ComputeKnowledgeHashAsync` 确定性重算比对，
    只出 `DailyRecapResponse.KnowledgeStale` 提示；今日水位自动重生成照旧，两把尺独立。
    `GetCachedDailyRecapAsync`（public 路径）零改动逻辑：不查知识、不重算、恒 false。
  - Probe 不是投影输入（结构性排除）；负向测试证明 Probe 增删不影响 hash。
  - 测试：`KnowledgeProjectionTests`（纯函数 13 例：边界日期、祖先链、去重、Episode、hash 稳定性/
    敏感性）+ RecapProjection digest 渲染 3 例 + RecapServiceTests 7 例（stale hint、无关变化、
    null hash、force 清除、失败保留、水位共存、public cache-only、Probe 排除）。全套 201 过。
  - 注意：`frontend/src/api/client.ts` 是 NSwag 生成物，`KnowledgeStale` 字段待前端接 UI 时从
    live OpenAPI 重新生成（issue 05/06 范畴）。
