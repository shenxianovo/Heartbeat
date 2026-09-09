# 删除 Fact 格式治理

Status: done

## 用户决策与边界

2026-09-09：删除 Fact Schema 文档注册/传播、版本/摘要字段、FactSchemas、格式演进基线、
mutablePayloadPaths 和专用校验依赖。Payload 保留 JSON，不以新的运行时规则注册表替代。
Fact 同版本内容直接比较，不保存内容哈希。保留权限隔离、基本身份/家族/时间/JSON/尺寸约束、
原子保管、准确 ACK、乱序与冲突保护。分表、时间精度及其他存储字段设计另行承接。
原项目未迁移线上快照禁止访问；测试仅使用 Testcontainers 独立随机端口数据库。

## 验收

- [x] 生产者、协议 SDK、Runtime、Analytics、Package staging 和未部署迁移移除格式治理。
- [x] 删除仅测试已取消规则的测试，保留包完整性和授权保护。
- [x] Collector → Hub → HTTP → Analytics 回归覆盖任意 Payload、扩展、修订、重放和隔离。
- [x] 相关全量自动验证通过，并记录证据。
- [x] glossary/ADR、架构和构建文档收敛；不恢复旧 storage-design.md。

## 验证

- `dotnet build Heartbeat.slnx --no-restore`：零 warning/error。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `dotnet test Heartbeat.slnx --no-restore`：13 个项目、1238 项通过；Server 503、Hub 310、System 89。
- 随后新增 4 项 Event 非输入 Payload 回归，`dotnet test server/Heartbeat.Server.Tests --no-restore --filter FullyQualifiedName~Fact`：41 项通过。
- 最后上传确认查找改为按身份索引直接比较内容，`dotnet test collection/hub/Heartbeat.Collection.Hub.Tests --no-restore --filter FullyQualifiedName~NativeUpload`：10 项通过。
- Browser `npm run build && npm test`：96 项通过；Frontend `npm run verify`：274 项通过、类型检查/构建通过。
- `node scripts/collector-contracts.mjs check`：Package 内容引用检查通过，不再读取格式基线。
- `git diff --check` 通过；代码/样例已无 Fact Schema 类型、版本字段或演进规则引用。

新纵切 `CollectorToAnalytics_UnknownPayloadSurvivesRestartRevisionReplayAndOwnerIsolation` 以 Segment/Event
两种家族从已验证 Package 激活 Collector，经 Runtime 持久化/重启和真实 HTTP 进入独立 PostgreSQL，
覆盖无业务投影的任意 JSON、字段扩展、Revision 更新、重复/冲突、乱序、迟到上传确认和 Owner 隔离。
`EventPayloadWithoutInputVocabulary_IsSavedAndRemovesStaleInputProjection` 覆盖数组、字符串、未知
code set 与非数字 code，确保正常事实不因报表不适用而丢失，也不留下过期输入计数。

## Friction closeout 与整合

ADR-040/041/054、领域词汇、架构说明、Package 文档与 CI staging 入口已同步；原 issue 01 的历史
Schema 验证结论已标记被本项替代。PRD 仍为 ready-for-human：生产备份迁移和现场 Collector
升级原有门禁由部署 owner 承接，本 issue 的代码与自动验证已完成，不表示部署完成。
旧格式 journal 的严格拒绝与原文件保留延续现有切换边界，见 runbook；本项不引入新格式注册表
或历史 journal 转换器。NativeFactCustody 尚未部署，因此直接收敛该迁移及快照，不修改已部署迁移。

分支 `codex/remove-fact-schema`，基于 `e82193c`（已删除 Fact 撤回）。不改原工作区、不部署、不推送、
不触发 CI；仅独立 Testcontainers 数据库，原项目线上快照未启动或访问。按家族分表和其他逐项
存储决定继续由原任务承接，整合时应保留其未提交 ADR-055 与脚本决定。
