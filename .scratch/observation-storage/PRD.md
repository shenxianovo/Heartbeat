# Observations 五表存储实施

Status: ready-for-human

2026-09-11 用户授权按五表方案及迁移映射实施。补充要求：能直接迁移就直接迁移，
不为区分新旧生造 legacy-* 分类、身份作用域或 Gap；确实缺失的信息如实保留未知。

依据：[ADR-059](../../docs/adr/059-observation-storage-five-tables.md)、
[五表方案](../../docs/architecture/observation-storage-minimal.md)、
[迁移映射](../../docs/architecture/observation-storage-migration.md)。

## 验收

- [x] Collectors、Objects、Facts、Relations、RelationMembers 持久化，单份事实结果。
- [x] 追加迁移保住旧行身份、上传身份、修订、时间、完整内容；旧缓存仍可重放。
- [x] FOI、关系成员与事实证据保持 Owner 隔离；同 App 跨设备的事实不串联。
- [x] 摄入、公开查询、使用者关联和产品纠错使用新持久化模型；原管理与交付职责保留。
- [x] 真实 PostgreSQL 的追加升级、HTTP 链路和相关回归通过；构建与命名检查通过。
- [x] Standards/Spec review 及文档收口完成。
- [ ] 完整已有数据副本、资源/恢复演练与生产切换另行验收；不恢复上一轮用户已暂停的操作。

## 验证范围

沿用已确认的 FactHttpTests、FactStoreTests 与 PostgreSQL migration fixture；
先失败后修复，围绕身份、原子修订、关系精确绑定、未知保全与 Owner 隔离验证。
自动验证不替代生产副本与实机/发布验收，不把上一轮未关闭门禁标 done。

## Comments

### 2026-09-11 实施与验证

- 追加 ObservationFacts / ObservationObjects 两项迁移。保留旧行 Id、完整上传复合键、Revision、
  时间及 JSON；Segments 原地演进为 Facts，Events 搬入后退役。跨家族行 Id 冲突先拒绝并回滚。
- 稳定对象引用落在原资料中；全局 App 与 Owner 私有对象分开约束。关系通过准确 Fact 引用绑定，
  同事务跟随修订；成员种类/数量/Owner、证据外键与并发编辑受数据库保护。
- 现有 Segment/Event 摄入、本人视图及设备/App/账号查询切到新表，保留平台应用证据与产品纠错。
  API 增加 FoiId/Aspect，使用隔离 Development OpenAPI 重新生成前端 client。
- 原上传转换、资料管理与交付路径继续支持现有生产者和离线缓存；退出门槛见迁移文档第 6 节。
  本轮未新建 Measurement 生产协议或原生对象管理 API；没有新增新旧身份分类或 Gap。

验证证据（本机 /tmp 日志为本轮辅助记录，行为测试已入仓）：

| 验证 | 结果 |
| --- | --- |
| `dotnet build Heartbeat.slnx --no-restore` 基线与后续 test 构建 | 通过 |
| `dotnet test Heartbeat.slnx --no-restore` | 1,327 通过，含服务端 578；日志 `/tmp/heartbeat-five-tables-solution-tests.log` |
| 新增 `ObservationFactsMigrationTests` 单项 | 通过；跨家族 Id 冲突拒绝、原行内容及 migration history 不变 |
| `PersonMigrationTests` | 两家族升级前后规范化全字段比较，重复迁移、未知身份与空使用者证据通过 |
| `ObservationStorageTests` / `FactHttpTests.Observations` | 重放、同 App 跨设备、修订、Owner、空 Evidence、成员移动、未知 FOI 应用筛选通过；关系和查询问题已先复现失败 |
| `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` | 通过 |
| `npm run typecheck`（frontend） | 通过 |
| 数据 smoke 与演练脚本 | Node/Python 语法检查通过；隔离 PostgreSQL 执行生成 SQL，零违规，并故意绑错设备证明脚本检出；临时验证 harness 已移除 |
| Standards / Spec review | 已完成；修复关系完整性、空证据、历史应用查询及迁移对照缺口，复核通过 |
| `git diff --check` 与本轮文档链接 | 通过 |

剩余承接：Owner 决定恢复已暂停的完整已有数据副本/资源与恢复演练，再执行生产切换；
上一轮真实 System/VRChat 安装验收仍由原 PRD 保管。当前 ready-for-human，未把自动测试当成上线验收。

### 2026-09-11 迁移结果逐类取样

用户要求查看前后数据库 diff。已在隔离 PostgreSQL 重建 9 条构造事实，覆盖四类 FOI、
Segment/Event、无 App、未知账号/Collector/FOI/Aspect；捕获真实 SQL 查询结果。
[可读报告](samples/REPORT.md)附 before/after JSON、seed SQL 和核对脚本。
9 条事实完整内容/身份/时间对照通过，5 条关系的具体成员与有效时间均对照原证据通过；
FactGaps 0→0。取样 harness 1/1 通过后移出正式测试目录。
这不是生产数据抽检；人工/资源门禁状态保持原样。Analytics 与 Dashboard 的解析职责正在讨论，未据此改动架构。
