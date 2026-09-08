# Analytics 原生 Fact

Status: ready-for-human

## 用户决策

2026-09-08：Analytics 改为原生 Fact；无损迁移，保留历史记录；初次实现不 Commit。
同日 review 后 owner 授权修复发现的问题并提交，Measurement 与生产部署仍不在本轮范围。

## 范围

Collector → Hub 已有 Fact Protocol。将 Hub → Analytics 的事实上传、持久化及 Dashboard 读模型
改为保留 Subject、Stream、schema、Revision 与完整 Payload；ActivitySegment/InputEvent 仅作读投影。
旧数据库、Runtime 状态与上传缓存均保留可恢复来源；确定性关联历史，避免升级重放重复计时。
Measurement、生产部署和实际用户数据库改写不在本轮实施范围。

## 实施

- [01 — 原生摄入、持久保管与历史迁移](issues/01-native-fact-migration.md)

## 验证

- 已建立基线：`dotnet tool restore` 成功；`dotnet build Heartbeat.slnx --no-restore` 零警告/错误；
  `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 通过。
- Review 修复后完整验证通过：.NET 13 个项目共 1255 项（Server 503、Hub 330）、前端 274、
  Browser 96；构建、命名与契约检查通过。详细命令、失败复现与结果见 issue 01。
- 生产部署前仍需真实数据库备份迁移演练与 Windows/macOS/Headless 升级 smoke，承接者为部署 owner。

## 设计

[ADR-054](../../docs/adr/054-native-analytics-fact-ingest.md)；
[升级与核对步骤](../../docs/runbooks/native-analytics-facts.md)
