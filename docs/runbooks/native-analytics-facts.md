# Analytics 原生 Fact 升级

对应 [ADR-054](../adr/054-native-analytics-fact-ingest.md)；执行与验收由部署 owner 承接，
证据记入 [迁移 issue](../../.scratch/native-analytics-facts/issues/01-native-fact-migration.md)。

## 严格切换边界（2026-09-09）

Fact 撤回与 Fact Schema 格式治理已删除，旧撤回字段、格式版本/文档与事实摘要均不属于
新契约。新 Collector/Package、Hub 与 Analytics 必须一起切换，不能把混用版本的拒绝视作成功上传。
含旧字段的 Runtime committed Fact 或 Collector outbox 会明确拒绝加载并保留原文件；
本次没有添加历史 journal 转换器。部署 owner 必须先核对实际安装的持久状态与未确认记录，
如存在该形状则保持旧版本保管数据，另行验证无损切换后再升级；不能删除状态文件绕过拒绝。
当前 NativeFactCustody 尚未部署，测试仅修改其建表定义；已经应用旧草案 migration 的临时库
不作为升级支持对象；不能对已应用草案的库仅覆盖迁移文件。真实快照仍停在 AskingWindowIdentity，本轮没有启动、迁移或修改该库。

## 升级顺序

1. 保存当前 PostgreSQL 完整备份及各 Desktop/Headless 的 Runtime、旧上传缓存和 dead-letter。
   在独立数据库恢复备份，记录原 ActivitySegments/InputEvents 的行数、时间范围及每日统计。
2. 在该副本应用当前 migration，并执行下列只读核对。当前 Analytics 仍按 ADR-013 在启动时自动
   应用 migration；启动新版服务就是执行升级，不能把连接真实库的启动当作无副作用预览。
3. Analytics 与 Dashboard 同次升级：新的活动 API 返回结构化 Payload，旧版 Dashboard 的
   Attributes 字符串读取不再适用。旧段/输入上传端点继续排空现有缓存。
4. 升级 Desktop/Headless。新生产数据走 `/api/v1/facts`，旧缓存继续排空；仅不含上述退役字段的
   Runtime state v1/v2 支持现有升级路径，并保留对应 `.v1.bak`/`.v2.bak`。核对原有设备名称和历史未被大小写不同的 UUID 拆开。
5. 让同一段活动跨多次上传、断网、重启和恢复连接，确认 FactId 不变、Revision 正常收敛、
   没有重复计时。分别检查 Browser 完整 URL、输入计数和 Headless Account 的 Subject 名称。
6. Desktop 上传状态及 Headless owner-only `GET /hub/api/v1/uploads` 应能区分 backlog、
   隔离记录和正常状态。409/422 的坏记录先持久隔离再确认，其他记录继续上传；确认隔离原因前
   不删除 `facts-dead-letter.json`。仍有未上传 Fact/Gap 时，Instance 移除会保留记录并要求稍后重试。

## 只读核对

先在尚未接入新 Collector 的迁移副本运行。两个 count 分别应等于迁移前的活动/输入总数；
档案缺失与孤立投影都应为 0。新原生数据到达后不能继续要求总 Fact 数等于原始历史行数。

```sql
SELECT "LegacyKind", count(*) AS imported,
       count(*) FILTER (WHERE "LegacyRecord" IS NULL) AS missing_archive
FROM "Facts"
WHERE "LegacyKind" IS NOT NULL
GROUP BY "LegacyKind";

SELECT 'activity' AS projection, count(*) AS orphaned
FROM "ActivitySegments" s LEFT JOIN "Facts" f ON f."Id" = s."FactKey"
WHERE f."Id" IS NULL OR f."OwnerId" <> s."OwnerId"
UNION ALL
SELECT 'input', count(*)
FROM "InputEvents" e LEFT JOIN "Facts" f ON f."Id" = e."FactKey"
WHERE f."Id" IS NULL;

SELECT s."Source", min(s."StartTime"), max(s."EndTime"), count(*)
FROM "ActivitySegments" s
GROUP BY s."Source";
```

抽查迁移前后的原始 URL、未知 attributes、输入 Code/CodeSet 和 Account 归属，并比较每日统计。
`LegacyRecord` 保留迁移前原始行，原生重放关联后仍保留。原生 Facts/FactGaps 的时间列为自公元 0001-01-01 UTC 起的 100ns ticks
（bigint），查询投影继续保存 PostgreSQL timestamp；不要将两者当作同类型直接比较。旧库没有存过的 Stream/Revision 不会被
伪造恢复。无需从标题或相近时间推断关联。

## 回退边界

只有 LegacyImport、尚无原生 Fact/Gap 的数据库支持当前 migration 的 Down→Up roundtrip；
Down 会还原原始 Attributes 和历史 Device 引用。接收过原生 Fact/Gap 后，Down 明确拒绝，避免
删除旧表无法表示的修订或独立 Subject。此时保留升级后的数据库与 Collector 缓存，
优先向前修复；如需恢复升级前备份，必须同时安排新增事实的保管和重放，不能直接覆盖并丢弃新增历史。

自动验证覆盖真实 PostgreSQL migration fixture、旧缓存/原生重放交错、幂等/冲突/纠正及旧撤回拒绝、
Owner/Subject 隔离以及 Runtime 的重启和精确确认；它不替代实际安装与真实备份的升级证据。
