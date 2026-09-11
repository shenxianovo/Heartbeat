# 05 — 归属切换验证与旧路径清理

**What to build:** 完成三个 Collector、存量事实及待发缓存的升级演练，使正常业务查询直接依赖 Observer 与 Target，并移除已经没有消费者的旧归属路径。

**Blocked by:** [01 — System 观测身份与设备归属完整链路](01-system-observer-target.md); [02 — Browser 窗口观测与应用上下文归属](02-browser-application-context.md); [03 — VRChat 账号观测与事实归属](03-vrchat-account-observation.md); [04 — 本人关联维护与追溯查询](04-person-associations.md).

Status: ready-for-human

**Parent:** [观测身份与事实归属改造](../PRD.md)

## 实施约束

这是扩展与迁移后的收尾任务，不是第一次考虑兼容。依据前四批记录逐项确认旧第一方 Collector/Runtime 输入、持久缓存、Browser 待发快照与 VRChat 检查点的处理方式。不得留下没有实际消费者、退出门槛或验证依据的兼容分支。

保留现有 Owner、Stream、FactId 的事实身份，不机械删除仍用于传输、Gap、ACK 或管理授权的 Stream/Subject 概念。业务查询不再依赖 Stream → Subject 解释归属；历史未知应能由直接存储的有效归属或明确未知状态表达。

遵循已有备份、升级和停写约定。完成演练与可审阅切换步骤；没有生产发布授权时不实施部署，也不将未执行的实际切换标成完成。

迁移入口采用 [ADR-058](../../../docs/adr/058-ci-database-migration.md) 与
[迁移 Runbook](../../../docs/runbooks/analytics-database-migration.md)：同一候选镜像在 CI 独立阶段
执行 `--migrate`（EF + C# 回填），成功后通过 `--check-database` 再以 Production 启动。
旧 Analytics 必须在存量回填前停写，不能在迁移与新版本启动之间恢复旧写入。
1C1G 迁移取消 SQL 命令超时；SSH/job 有长时间上限，不将旧十分钟预算继续作为发布门禁。
本任务记录实际停写时间、资源峰值、失败重试与升级前备份恢复证据。

## Acceptance criteria

- [x] 三个 Collector 的新写入均使用 Observer/Target 契约，活动、输入、账号、App、本人及相关报表的业务归属查询已切换。
- [ ] 使用既有数据的隔离副本完成迁移演练，对照家族行身份、数量、时间、Payload、Revision 与代表性查询，记录差异及解释。
- [ ] 用同一候选镜像验证独立迁移与生产启动、失败不启动/重试、备份恢复及 1C1G 实际耗时；不以 Development 自动迁移代替发布路径验收。
- [x] 旧 Runtime 缓存、Browser 待发快照和 VRChat 检查点有已验证的升级/重放路径；旧新快照同事实收敛，不双写或丢失。
- [x] 验证不同 Stream 中相同 FactId 仍是不同事实，同 Target 不导致归并；未知历史无需恢复旧归属查询即可读取。
- [ ] 已完成可映射存量回填，无法可靠恢复的 Observer/账号/App 信息明确保留未知，不靠当前配置推断历史。
- [x] 逐项记录旧入口的消费者与退出证据，删除已无消费者的旧业务归属回退及转换；仍需保留的离线升级路径有明确用途。
- [x] 保留的 Stream、Source 或管理 Subject 用途与当前模型一致，文档和术语不再把它们描述为新的事实业务归属。
- [x] 完成跨项目回归、构建及命名检查；自动、演练与实际资源验收分别记录，真实剩余门禁保持可追踪状态。
- [x] 交付备份/停写/升级/回放/查询对照/恢复步骤与演练结果，完成 review、提交及整个功能 tracker 的 lifecycle closeout。

## Validation

运行最高可用的 Collector → Runtime → Analytics HTTP → 查询链路，以及从已部署家族基线到最新结构的真实 PostgreSQL 升级验证。对照新旧查询的代表性结果，明确哪些差异来自已确认的新归属语义，哪些应当完全一致。

清理前检索实际调用与查询路径；清理后通过构建和链路回归证明消费者已迁移。恢复步骤必须与实际演练的版本及数据格式对应，不用“必要时回滚”代替可执行说明。

本任务不能用通过空库测试代替存量演练。需要实际生产切换而尚未获授权时，完成其他工作并记录真实门禁，不能擅自部署或将功能标 done。

## Comments

2026-09-10：只有兼容退出证据齐备才删除对应路径；历史传输身份不在本次清理目标内。

2026-09-10（CI 迁移方式调整）：System/Browser 的追加 migration 与业务模型不变，变更发布顺序。
迁移入口及 CI 编排单独实现；完整存量副本、真实资源和生产切换仍由本任务承接，状态与原验收
保持未完成。本次调整没有执行生产迁移，也没有关闭前四批的真实资源门禁。


2026-09-11（实施、验证与暂停门禁）：

- 当前代码完成直接 Target 查询切换：家族 API、活动/输入投影、Experience、Recap/Question 与
  Dashboard 删除旧 Subject 归属回退和 JSON 别名；OpenAPI 客户端同步。追加
  `20260911004949_CompleteHistoricalTargets` 仅回填剩余已有 Machine/Device 证据的 null Target，
  旧迟到输入采用同样规则；不改已部署 migration、家族身份、Revision、时间或 Payload。
- 新风险先红后绿：自定义来源 Segment/Event 缺 device Target（2 项失败）、未知历史 HTTP
  仍输出 Subject 别名（1 项失败）。旧测试 fixture 切换为明确 Target，设备先持久化再引用。
- 公开命令失败路径正常返回 1，避免受限 Linux 下异常日志已打印但未及时退出；进程回归先确认
  原退出 134，再通过 `--migrate` / `--check-database` / Production 失败不监听且不改 schema 检查。
- .NET 第一轮及命令修复后的最终回归均为 1,323/1,323 通过。Frontend 295、Browser 111、
  Python 部署脚本 11、Node 脚本 11 全通过，前端类型检查、两端构建及 .NET 命名检查通过。
- 真实 Google Chrome headless 两窗口 → macOS Desktop → 独立 Analytics 成功；窗口关闭后历史
  可读、设备/App 联合查询、Chrome/Desktop 都重启后的离线补传通过。报告在
  `.local/observation-cutover-05/browser-smoke.log`，不扩大为人工可见窗口或 Windows 验收。
- 仅复制实际 Runtime 文件到私有隔离目录，Desktop v5（1,370 条 Facts）、Headless v3（21 条）
  均可升级 v6/再次打开，原备份和保管字段保持，Payload 只有已记录的 identityKey→activityKey
  规范化。两份实际队列当前 pending=0；非空待发重放由 Runtime/Browser/VRChat 自动 fixture
  和上述真实 Browser 离线链路证明，不能把空实际队列称作待发重放。原文件未修改。
- 用户重新拉取生产库后重新做只读备份，基线 NativeFactCustody，223,313 Segments、1,939,969
  Events，85,661,732 bytes，SHA-256
  `e01e34b2e93beb134818412d7b1dd6cf4e0e0101abc303ee5b7518d3a959f1e1`。
  失败/暂停过程与验收边界见 [脱敏报告](../cutover-report.json)。
- 512 MiB 恢复多次 OOM；降低维护内存、分连接和 checkpoint 未使其可靠。768 MiB 恢复成功，
  但这与 Analytics 256 MiB 已合计 1 GiB，不是完整 1C1G 整机通过。完整前后逐行核对、最终候选
  镜像重试/启动和升级前备份恢复尚未完成，前三项及“存量回填完成”不勾选。
- 用户要求暂停受限迁移，已中断 run-07 并清理本次隔离迁移资源，未执行生产部署。
  01 的 System/Windows、03 的 VRChat 真实账号门禁仍为 ready-for-human。旧离线 reader、
  checkpoint 与摄入 adapter 保留实际消费者和退出条件，不机械删除传输/管理 Subject。
- 实施与存储审阅：[当前存储说明](../../../docs/architecture/observation-target-cutover.md)；
  备份/停写/升级/重放/查询/恢复步骤：[runbook](../../../docs/runbooks/observation-target-cutover.md)。
  Standards 与 Spec 独立审查及命令入口补审均为 0 项确认缺陷，不将代码审查当作实际演练通过。

Friction closeout：修正旧 System/Browser 文档中的十分钟门禁及过渡查询描述；保留用户原有模型
文档修改；将完整副本、资源、安装与生产门禁明确区分，没有将功能标 done。Review 和提交属于
本批交付；实际演练的暂停不隐藏在“代码完成”中。
