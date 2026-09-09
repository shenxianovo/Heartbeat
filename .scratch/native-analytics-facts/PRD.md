# Analytics 原生 Fact

Status: ready-for-human

## 用户决策

2026-09-08：Analytics 改为原生 Fact；无损迁移，保留历史记录；初次实现不 Commit。
同日 review 后 owner 授权修复发现的问题并提交，Measurement 与生产部署仍不在本轮范围。

2026-09-09：owner 要求按最小事实模型删除 Fact 撤回；代码、必要回归与文档已完成。
Fact 身份及正常修订继续保留；分表、时间列、Id/FactId 与 Revision 起点的独立设计不在此次范围。

2026-09-09：owner 另行要求删除 Fact Schema 格式治理与 Fact 内容哈希，实施与证据见
[02 — 删除 Fact 格式治理](issues/02-remove-fact-schema.md)。分表设计仍独立承接。

## 范围

Collector → Hub 已有 Fact Protocol。将 Hub → Analytics 的事实上传、持久化及 Dashboard 读模型
改为保留 Subject、Stream、Revision 与完整 Payload；ActivitySegment/InputEvent 仅作读投影。
旧数据库、Runtime 状态与上传缓存均保留可恢复来源；确定性关联历史，避免升级重放重复计时。
Measurement、生产部署和实际用户数据库改写不在本轮实施范围。

## 实施

- [01 — 原生摄入、持久保管与历史迁移](issues/01-native-fact-migration.md)
- [02 — 删除 Fact 格式治理](issues/02-remove-fact-schema.md)

## 验证

- 已建立基线：`dotnet tool restore` 成功；`dotnet build Heartbeat.slnx --no-restore` 零警告/错误；
  `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 通过。
- Review 修复后完整验证通过：.NET 13 个项目共 1255 项（Server 503、Hub 330）、前端 274、
  Browser 96；构建、命名与契约检查通过。详细命令、失败复现与结果见 issue 01。
- 生产部署前仍需真实数据库备份迁移演练与 Windows/macOS/Headless 升级 smoke，承接者为部署 owner。

- 2026-09-09 删除撤回后的全量 .NET 13 项目 / 1257 tests 通过，新增 HTTP 拒绝回归 3 tests
  单独通过；前端 274、Browser 96 tests 与构建通过。现场旧 Runtime/outbox 的严格切换边界已记入
  runbook，部署 owner 完成实际持久状态核对与无损切换前仍保持 ready-for-human。

## 设计

[ADR-054](../../docs/adr/054-native-analytics-fact-ingest.md)；
[升级与核对步骤](../../docs/runbooks/native-analytics-facts.md)

2026-09-09 格式治理删除完成：13 个 .NET 项目 1238 项通过，新增消费者边界用例后 Fact 定向 41 项通过；
Browser 96、Frontend 274 项与构建通过。详见 issue 02；生产升级门禁保持 ready-for-human。
