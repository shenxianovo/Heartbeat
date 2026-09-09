# ADR-054: Analytics 原生 Fact 摄入与无损历史迁移

## Status: Accepted

2026-09-09：下文通用 Facts、完整查询投影、永久整行档案、Segment/Event 的 100ns 存储及
服务端终态校验已由 [ADR-055](055-fact-storage-by-family.md) 替代。当前代码直接保存家族事实，
活动/输入结果在查询时生成；终态归 Collection/Runtime，Analytics 只比较已保存时间和 Payload。
下文保留原方案记录，不作为现行迁移执行步骤；实际部署与资源演练仍未完成。

## Date: 2026-09-08

2026-09-09：遵循 ADR-041 修订，删除 Fact 撤回；摄入只接受完整 payload 快照，存储不再有
RecordState 或墓碑。本次直接修正尚未部署的 NativeFactCustody migration，不改写已部署历史
migration，不实施分表、时间存储、Id/FactId 或历史 Revision 的独立设计调整。

## Context

Collector Protocol 已有 Subject、Stream、FactId、Revision 语义，但 Hub 上传前仍把
Segment 投影成 ActivitySegment、Event 投影成 InputEvent。Analytics 因而无法识别修订与
Stream；完整 payload 被放进旧 Attributes 后，Dashboard 还需要猜测 JSON 包装层才能显示网址。
这使 ADR-041 的事实语义在采集与分析之间丢失，也让 Account 继续借用 Device。

Owner 已选择迁移到 Fact，并明确要求无损迁移、保留历史记录。旧表没有保存过的 Stream 与
Revision 不能从标题、时间或 URL 推测恢复。

## Decision

Collection → Analytics 使用自包含的原生 Fact 批次。每批携带用到的 Subject、Stream
定义，以及 Segment/Event 快照与 Gap。Owner 只取自
认证身份。Machine 可以关联 Device，Account/Person 独立存在，不制造硬件归因。

Analytics 以 Owner + StreamId + FactId 识别事实，原子接收整个批次。同一 Revision 的相同内容
幂等，内容不同冲突，低 Revision 不覆盖高 Revision；高 Revision 保持 Segment 起点、Event 发生时间和 Segment 终态。Segment 的合法纠正同时更新有效读投影，不能再用 EndTime 取 max 代替修订。
Segment/Event 的 Payload 均允许正常修订，不登记可变路径。Measurement 继续等待真实 Collector，不在本次预建。

Fact 是写入权威。ActivitySegment/InputEvent 保留为同一事务内维护的查询投影，使现有报表、
Matcher、Recap 与回放保留成熟的 SQL 查询入口。Dashboard 接收结构化 Payload 及 Fact/Subject
元数据，不再从旧 Attributes 字符串猜测完整 Fact。投影不拥有独立事实写入口。

原生 Fact/Gap 的时间以 UTC 100ns ticks（bigint）持久化，保留 Collector 上报精度；查询投影继续
使用 PostgreSQL timestamp。不能把落库后的微秒截断值与原始时间比较来判断修订或 Gap 冲突。
仅 LegacyImport InputEvent 接管按旧 Npgsql 的微秒编码核对时间，因为旧库已丢失更细精度；
接管后原生时间与后续修订仍严格、无损比较。

2026-09-09 按 owner 决策删除 Fact Schema 注册、运输、版本锁定与演进校验，详见 ADR-041 修订。
Fact 不保存内容哈希；同版本幂等直接比较已保存的事实时间、终态和 JSON 内容（忽略 ObservedAt）。
Payload 的新增字段无需改包格式声明；报表不适用的事实仍完整入库，不制造替代规则注册表。

Hub 已提交的 Runtime 状态承担原生上传的持久保管；Analytics 成功后只确认本批相应版本。
上传期间到达的新修订继续待传。未确认的事实与 Gap 不因容量驱逐或卸载 Instance 被删除。
原有 segment/input 缓存仅用于排空升级前数据。
旧 Runtime 中缺少 GapId 的 Gap 在备份后分配并持久保存一次身份；旧 outbox 的 lost-ACK 重试
仅能按完整内容绑定一次别名，不能为同一已迁移 Gap 新建第二条上传记录。

历史表逐行导入明确标记的 LegacyImport Fact，保留原始记录档案及原有查询身份。旧数据缺失的
修订来源不伪装成 Collector 元数据。对于升级后 Runtime 重放，只有旧 projector 的确定性 Id
及 Owner/Subject/Source 等身份完全匹配，才建立原生事实与历史导入的关联；不按相近时间或标题
去重。关联后迟到的旧缓存不能覆盖原生修订。

旧 `/segments` 与 `/input-events` 路径在升级期只作为 LegacyImport adapter，不再直接写投影。
这修订 ADR-020 的唯一段上传入口，并为 ADR-035 的严格单版本切换增加有明确服务对象的缓存
排空边界；不恢复 AppName 等已退役字段兼容。退出门槛见兼容台账。

## Consequences

- Fact 的身份与修订可以贯穿采集、持久化和查询。
- 历史记录可追溯，迁移与重放不会依赖模糊推断；旧版本丢弃的元数据无法凭空补回。
- 读投影保留查询效率，但必须与 Fact 同事务更新，新增写路径不能绕开 Fact Store。
- 上线需先备份并演练现有数据库、旧缓存与 Runtime 状态；代码与自动验证完成不等同于生产迁移完成。

## References

- [ADR-041](./041-unified-observation-fact-model.md)
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) — 旧快照生长语义
- [ADR-035](./035-strict-ingest-contract-and-versioned-cache-migration.md)
- [兼容债务台账](../architecture/compatibility-debt.md)
- [实施与验证](../../.scratch/native-analytics-facts/PRD.md)
