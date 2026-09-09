# Analytics 原生 Fact 升级

当前布局见 [ADR-055](../adr/055-fact-storage-by-family.md)，逐字段核对依据见
[迁移映射](../../.scratch/native-analytics-facts/migration-mapping.md)，验证证据记入
[issue 01](../../.scratch/native-analytics-facts/issues/01-native-fact-migration.md)。

## 当前实现与剩余验收

未部署的 `20260908141403_NativeFactCustody` 已直接替换：旧 ActivitySegments/InputEvents
原地改为 Segments/Events，保留行 Id，不创建通用 Facts，也不永久复制 LegacyRecord。
同一个迁移编号只执行新的实现；以前已应用旧草案的实验库必须从独立备份重建。

自动测试使用 Testcontainers 的独立随机端口数据库。实际线上克隆的完整数据 diff、
1C1G 资源/空间演练、10 分钟整体停服预算、备份保留和真实安装切换仍待验收，生产发布未执行。
下面是新布局的核对步骤，不代表部署脚本和资源预算已经通过验收。

## 严格切换边界（2026-09-09）

Fact 撤回与 Fact Schema 格式治理已删除，旧撤回字段、格式版本/文档与事实摘要均不属于
新契约。新 Collector/Package、Hub 与 Analytics 必须一起切换，不能把混用版本的拒绝视作成功上传。
含旧字段的 Runtime committed Fact 或 Collector outbox 会明确拒绝加载并保留原文件；
本次没有添加历史 journal 转换器。部署 owner 必须先核对实际安装的持久状态与未确认记录，
如存在该形状则保持旧版本保管数据，另行验证无损切换后再升级；不能删除状态文件绕过拒绝。
当前 NativeFactCustody 尚未部署，已替换为原地分表迁移；已经应用旧草案 migration 的临时库
不作为升级支持对象；不能对已应用草案的库仅覆盖迁移文件。真实快照仍停在 AskingWindowIdentity，本轮没有启动、迁移或修改该库。

## 独立副本演练

1. 保存升级前 PostgreSQL 完整备份与各 Desktop/Headless 的 Runtime、旧缓存和 dead-letter。
   在独立数据库恢复；记录迁移历史、旧表行数、时间范围、应用映射、Owner/Subject 和输入计数。
   保留最近两份成功升级前备份，以及尚未解决的失败升级备份；成功核对后才清理更旧成功备份。
2. 核对副本停留 AskingWindowIdentity、唯一待执行迁移是 NativeFactCustody，再执行升级。
   Analytics 启动仍自动应用 migration，不能把连接真实库的启动用作预览。
   EF 设计时工厂仅用于不启动应用的模型/SQL 生成；不会自动读取真实数据库连接配置。
3. 在接入新 Collector 前，按映射逐条比较历史 Id、Source、归属、时间、应用、活动分组、标题、
   Payload/未知字段、输入 Code/CodeSet，并比较查询结果。只核对总行数不算无损迁移通过。
4. 核对数据库、WAL、临时文件及内存峰值、迁移到健康可用的总耗时和重启；原地 UPDATE 仍有
   数据页与索引成本。当前 900 秒 SQL 命令超时不能替代 10 分钟整体停服预算。
5. 在副本验证同一事实多次上传、断网/重启、原生纠正和迟到旧缓存。分别核对 Browser URL、
   输入计数与 Account 归属，确认无重复事实且未确认版本仍由 Runtime 保管。

## 新布局只读核对

在步骤 3 中，两个行数应等于本次备份的原活动/输入行数；新事实到达后不能继续使用这个等式。
旧取证基线为 220,146 / 1,891,698，不应硬编码成所有备份的期望值。

```sql
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
SELECT to_regclass('public."Facts"') IS NULL AS no_universal_facts;
SELECT 'segment' AS family, count(*) FROM "Segments"
UNION ALL SELECT 'event', count(*) FROM "Events";

SELECT table_name, column_name, udt_name
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name IN ('Segments', 'Events')
ORDER BY table_name, ordinal_position;

SELECT 'segment' AS family, count(*) AS invalid_links
FROM "Segments" f LEFT JOIN "Streams" s
  ON s."OwnerId" = f."OwnerId" AND s."StreamId" = f."StreamId"
LEFT JOIN "Subjects" subject
  ON subject."OwnerId" = s."OwnerId" AND subject."SubjectId" = s."SubjectId"
WHERE s."StreamId" IS NULL OR subject."SubjectId" IS NULL
  OR s."Source" <> f."Source" OR s."FactKind" <> 'segment'
UNION ALL
SELECT 'event', count(*) FROM "Events" f LEFT JOIN "Streams" s
  ON s."OwnerId" = f."OwnerId" AND s."StreamId" = f."StreamId"
LEFT JOIN "Subjects" subject
  ON subject."OwnerId" = s."OwnerId" AND subject."SubjectId" = s."SubjectId"
WHERE s."StreamId" IS NULL OR subject."SubjectId" IS NULL
  OR s."Source" <> f."Source" OR s."FactKind" <> 'event';
```

Segments 为 10 列、Events 为 9 列；家族时间为 timestamptz，Payload 为 jsonb，invalid_links
应为 0。FactGaps 保留原 tick 精度，不属于此次家族表时间转换。
Unknown Payload 可以完整保存但不适用于某个报表，因此活动/输入查询结果数量不总等于家族总数。

## 回退与现场切换

本迁移 Down 明确拒绝：已删除冗余物理列，不再承诺用永久整行档案重建旧布局。
回退使用升级前完整备份；接收过新事实后，先保管升级后的数据库、已确认新增数据与未确认缓存，
安排恢复/重放，不能直接覆盖而丢失新数据。失败事务本身应回滚，冲突 fixture 覆盖旧行保留。

部署 owner 在副本与预算验收通过后安排 Analytics/Dashboard 和各实际安装切换。
旧上传端点只排空既有缓存；新数据经 `/api/v1/facts`，成功响应只确认相应版本。
409/422 隔离数据应可见且保留，不能删除 dead-letter 绕过冲突。IsFinal 由 Collection/Runtime
保管和校验，Analytics 不增加对应存储；字段改名不修改已保管的 Runtime 快照或生成新的 FactId。

自动 fixture 不替代真实安装升级、完整备份恢复与生产发布证据。完成前 issue/PRD 保持非 done。
