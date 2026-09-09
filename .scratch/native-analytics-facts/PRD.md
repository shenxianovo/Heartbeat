# Analytics 原生 Fact

Status: ready-for-agent

## 当前进度（2026-09-09，迁移替换完成，资源演练待做）

已核对当前代码与既有快照取证，完成 [旧字段映射与身份衔接](migration-mapping.md)。
用户再次强调第一性原理与奥卡姆剃刀：优先演进旧表、复用现有身份，不预建额外关联表或状态。
Owner 随后授权替换。未部署的 NativeFactCustody、Designer 和 ModelSnapshot 已同步替换，
旧表原地演进为 Segments / Events，保留原 Id；没有中间通用 Facts、永久整行档案或新别名表。
实体、摄入与 SQL 查询已接通，IsFinal 由 Collection/Runtime 负责。独立测试验证唯一待执行
迁移、表 OID 不变、10 / 9 列、时间类型、历史/重放/查询与失败回滚。
最终 13 个 .NET 项目 1259 项、Frontend 274 项、Browser 96 项通过；构建、命名、契约与 EF
模型一致性检查通过。详细证据见 issue 01。仅操作独立测试库，未访问原快照数据库或部署。
下一步仍是完整备份副本的数据 diff、1C1G 演练及部署预算/备份策略验收；整体不是 done。
以下按时间保留的记录不代表当前分表方案已经完成生产验收。

## 用户决策与历史记录

2026-09-08：Analytics 改为原生 Fact；无损迁移，保留历史记录；初次实现不 Commit。
同日 review 后 owner 授权修复发现的问题并提交，Measurement 与生产部署仍不在本轮范围。

2026-09-09：Owner 要求按线上克隆和 1C1G 约束准备部署，随后确认改为按 Fact 家族分表，
通过 grill-with-docs 收敛设计。原通用表部署修复暂停扩展；当前等待设计取舍，尚未实施分表。

本轮已确认：一份完整 Payload + 少量查询列；不保留旧修订或永久整行档案；升级停服上限
10 分钟；保留最近两份成功升级前备份及未解决失败备份。允许改进数据库命名，并要求
最终提供完整字段与真实数据 diff。旧完整存储提案已按 Owner 要求删除；当前在对话中逐项审阅，
只记录已确认决定，不能把文档提案当成已迁移。

2026-09-09 09:56：拆开刷新与启动后，Owner 已重新取得未迁移的线上快照。只读核对确认
最后迁移为 AskingWindowIdentity、无 Facts 表；220,146 条活动、1,891,698 条输入、约 365 MiB。
20 张既有表的字段清单与两个真实样例已重新核对，设计文档统计已替换为此基线。
此前混合本地库的性能实验仅保留为问题定位证据，不能替代本次原始快照的受限资源验收。

已确认的逐项决定以 [ADR-055 的决定表](../../docs/adr/055-fact-storage-by-family.md#逐项审阅决定2026-09-09) 为准。
撤回删除已在独立会话完成并合入本地 main；Fact Schema 机制删除已完成，提交 feb85bb 已整合到当前实施分支。
当前存储讨论继续，最终映射与样例待决定收敛后整理，尚未进入分表实现。

本轮收敛：Owner 要求先确定核心 Fact 模型，其他内容后续慢慢讨论。
[核心结构](../../docs/adr/055-fact-storage-by-family.md#本轮确定的核心-fact-模型) 为 Segments 10 列、Events 9 列，
通过 Stream 关联 Subject、通过可选 AppIdentity 关联应用；不再扩展附加状态或协议治理字段。
本次仅确定并记录模型，迁移实现、完整数据映射与资源验收仍未完成。

Owner 随后授权开始实施并先审查未提交改动。已隔离旧通用 Facts 的迁移/部署实验，保留并验证
刷新与启动分离等基础修复，核清 38 条 App 映射残留；详细证据见 issue 01。
Schema 删除已整合，当前开始实体、摄入、查询和迁移的分表改造，尚未宣告完成。

2026-09-09：owner 要求按最小事实模型删除 Fact 撤回；代码、必要回归与文档已完成。
Fact 身份及正常修订继续保留；分表、时间列、Id/FactId 与 Revision 起点的独立设计不在此次范围。

2026-09-09：owner 另行要求删除 Fact Schema 格式治理与 Fact 内容哈希，实施与证据见
[02 — 删除 Fact 格式治理](issues/02-remove-fact-schema.md)。分表设计仍独立承接。

## 范围

围绕核心 Fact 模型保留 Subject、Stream、Revision 与完整内容，取消无当前必要性的 Fact Schema 治理体系，
改为 Segment/Event 按家族持久化，避免通用 `Facts` 与完整旧表投影重复保管同一内容。
历史数据无损迁移、升级备份和旧缓存确定性关联继续纳入范围；具体保留边界、停机预算和
存储布局见 [ADR-055 草案](../../docs/adr/055-fact-storage-by-family.md)。Measurement 不预建。
最终需完成线上克隆的受限资源演练和部署流程验证；实际生产发布尚未执行。

## 实施

- [01 — 原生摄入、持久保管与历史迁移](issues/01-native-fact-migration.md)
- [02 — 删除 Fact 格式治理](issues/02-remove-fact-schema.md)

## 验证

以下是原通用表方案的历史验证记录，不代表新的分表方案通过验收。当前线上克隆演练已暴露
命令超时、受限内存 OOM 与存储放大；旧方案的完整容器演练尚未通过全部核对。

- 已建立基线：`dotnet tool restore` 成功；`dotnet build Heartbeat.slnx --no-restore` 零警告/错误；
  `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 通过。
- Review 修复后完整验证通过：.NET 13 个项目共 1255 项（Server 503、Hub 330）、前端 274、
  Browser 96；构建、命名与契约检查通过。详细命令、失败复现与结果见 issue 01。
- 生产部署前仍需真实数据库备份迁移演练与 Windows/macOS/Headless 升级 smoke，承接者为部署 owner。

- 2026-09-09 删除撤回后的全量 .NET 13 项目 / 1257 tests 通过，新增 HTTP 拒绝回归 3 tests
  单独通过；前端 274、Browser 96 tests 与构建通过。现场旧 Runtime/outbox 的严格切换边界已记入
  runbook，部署 owner 完成实际持久状态核对与无损切换前不得部署；整体分表工作仍保持 needs-info。

## 设计

[ADR-055 草案](../../docs/adr/055-fact-storage-by-family.md)；
[ADR-054 原方案](../../docs/adr/054-native-analytics-fact-ingest.md)；
[升级与核对步骤](../../docs/runbooks/native-analytics-facts.md)

2026-09-09 格式治理删除完成：13 个 .NET 项目 1238 项通过，新增消费者边界用例后 Fact 定向 41 项通过；
Browser 96、Frontend 274 项与构建通过。详见 issue 02；生产升级门禁保持 ready-for-human。
