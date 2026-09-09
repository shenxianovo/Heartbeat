# Analytics 原生 Fact 升级

当前布局见 [ADR-055](../adr/055-fact-storage-by-family.md)，逐字段核对依据见
[迁移映射](../../.scratch/native-analytics-facts/migration-mapping.md)，验证证据记入
[issue 01](../../.scratch/native-analytics-facts/issues/01-native-fact-migration.md)。

## 当前实现与剩余验收

`20260908141403_NativeFactCustody` 在部署前已直接替换：旧 ActivitySegments/InputEvents
原地改为 Segments/Events，保留行 Id，不创建通用 Facts，也不永久复制 LegacyRecord。
同一个迁移编号只执行新的实现；以前已应用旧草案的实验库必须从独立备份重建。

自动测试使用 Testcontainers 的独立随机端口数据库。2026-09-09 独立完整快照演练已通过：
220,146 / 1,891,698 行及重启后逐行零差异；受限数据库/Analytics 中备份至健康 48.337 秒，
重启至健康 5.534 秒。证据见 issue 01 和 `.local/verification/fact-family-rehearsal/run-05/report.json`。
生产 c2fb3a2 已于 2026-09-09 完成该迁移（767.3 秒），重启确认无待执行迁移，Owner 已报告
服务与查询恢复。实际耗时超过原定 10 分钟预算；部署健康等待提前失败、App Catalog 启动查询
超时及周报查询超时均已出现，性能根因未确认。备份保留和真实 Collector 安装切换仍待验收。

## 严格切换边界（2026-09-09）

Fact 撤回与 Fact Schema 格式治理已删除，旧撤回字段、格式版本/文档与事实摘要均不属于
新契约。新原生 Fact 上传链路的 Collector/Package、Hub 与 Analytics 需要使用匹配契约，
不能把混用版本的拒绝视作成功上传。已发布 Desktop/Hub 仍走保留兼容的 segments/input-events
上传端点，因此可以先单独部署 Analytics，无需同时升级 Headless 或修改其 Runtime JSON。
含旧字段的 Runtime committed Fact 或 Collector outbox 会明确拒绝加载并保留原文件；
本次没有添加历史 journal 转换器。部署 owner 必须先核对实际安装的持久状态与未确认记录，
如存在该形状则保持旧版本保管数据，另行验证无损切换后再升级；不能删除状态文件绕过拒绝。
当前 NativeFactCustody 的原地分表实现已部署，后续不能再覆盖修改该迁移；已经应用旧草案 migration 的临时库
不作为升级支持对象；不能对已应用草案的库仅覆盖迁移文件。来源快照仍停在 AskingWindowIdentity，
独立演练阶段仅做只读 pg_dump；随后 Owner 运行 StartLocal，已启动并迁移本地栈。

## 独立副本演练

先构建候选镜像，再将旧布局的完整 custom-format `pg_dump` 交给可重复入口：

```sh
docker build -f server/Dockerfile -t heartbeat-fact-rehearsal .
python3 scripts/rehearse-fact-migration.py \
  --backup /absolute/path/to/before.dump \
  --image heartbeat-fact-rehearsal \
  --output .local/verification/fact-family-rehearsal/run-01
```

输出目录必须不存在。脚本仅从备份恢复独立 PostgreSQL，不读取 `.env.local`，不连接来源库；
网络为 Docker internal，不接入真实 Auth 或 Collector。数据库限制 0.75 CPU / 512 MiB，
Analytics 限制 0.25 CPU / 256 MiB，均禁用额外 swap。剩余 256 MiB 只是 1 GiB 预算中的预留，
不能据此声称已验证实际宿主机、Frontend 和 Collector 的总内存。

脚本以真实应用启动执行迁移。按 Id 游标每批 10,000 行，先取有界记录再关联和序列化，
比较总行数也必须等于来源表计数。对全部行进行精确比较，覆盖 Payload、Owner、Subject、
Stream、应用归属、时间和输入编码，另比较活动/输入聚合与重启后的全部行。
不为核对改变 JIT 或查询并行配置。迁移内部仅用 `SET LOCAL` 关闭本次建索引的并行工作者；
长 SQL 按六个阶段分别执行，仍受 EF 同一个事务保护，中后段失败会整体回滚。
报告只有汇总；备份、完整行导出及可能含私有内容的日志保存在忽略的输出目录，不提交。
完成或失败后清理本次创建的容器、卷和网络，保留备份及报告用于复核。

600 秒计时包括受限容器中的升级前备份、备份目录校验、候选启动迁移至 `/health` 成功。
镜像准备、初次恢复和核对发生在计时外。这是隔离环境的停写模拟，不能代替真实部署的停写、
备份保留、路由切换及 Collector 暂存/恢复验收。

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
