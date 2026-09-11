# 03 — 旧事实与旧缓存无损接管

**What to build:** 已有数据库、通用 SDK/Runtime 缓存和进行中旧事实升级后进入统一观测核心。无论导入或重放先后，同一事实始终保持已有行身份及最新内容，历史未知保持未知。

**Blocked by:** [02 — 原生观测的持久保管与交付](02-native-observation-delivery.md).

Status: done

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [x] 盘点仍支持的数据库基线、旧上传形状、SDK/Runtime 缓存和进行中事实；每种形状有确定性转换及代表性 fixture，专有 System/Browser/VRChat 恢复流程分别由 04–06 补全。
- [x] 旧输入先完成明确的身份与语义转换再进入同一核心；不能通过 Relations/FOI/Aspect 缺失隐式放开原生要求，也不能把旧原生快照永久当作另一套事实语义。
- [x] 旧事实保留数据库行 Id，完整 Owner/Kind/Stream/FactId 确定对应；两流同 FactId、不同家族和不同 Owner 不误合并，不直接用旧客户端 FactId 替换行主键。
- [x] 旧导入先到、原生重放先到、旧缓存迟到及重复升级均只保管对应的一条事实；已有修订后的区间不会被迟到旧记录拉长，独立 Id 冲突不丢行。
- [x] 新旧表示收敛不增加 Revision，同版本比较转换后的完整语义；已有历史未知的可靠补全通过兼容规则处理，不形成新原生事实更换固定属性的通道。
- [x] 旧缓存、Delivered/待发状态、进行中事实、Result 未知字段、时间和 Gap 保全；旧事实正常终结或确认前保持旧身份对应，新事实使用新契约。
- [x] 不可靠的 Collector/FOI/Aspect 如实保持未知，可靠信息按既有业务规则恢复；Source、平台 AppIdentity 及旧引用依据保留，不制造新分类、命名空间或 Gap。
- [x] 在真实隔离 PostgreSQL 上验证追加迁移的逐行身份/完整 JSON/Revision/家族时间/关系证据保全、失败回滚与重试；保留已发布迁移，不执行业务库迁移或暂停的完整副本演练。
- [x] 缓存升级失败保留原状态与恢复依据，重复读取不改变已确定的 Id/版本；旧包启动或回退不能消费新状态，新的完整 ACK 仍只确认准确快照。
- [x] 每条保留兼容有真实消费者、支持状态、退出门槛与验证方式；数据迁移到新格式本身不等于允许删除所有历史恢复能力。
- [x] 通用升级、历史接管、迁移及重启回归通过并记录证据，明确 04–06 仍需完成的专有安装状态验证。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。

### 2026-09-11 实施与基线

独立工作区 `/Users/bytedance/.codex/worktrees/0a0f/Heartbeat`，分支
`codex/observation-ticket-03`。初始化验证已验收 02/07/08 补丁 SHA256
`c536436f2b3f5cbfd8fbc078168aeff46e9c250050b7256240ccd4d1f3186801`，先经
`git apply --check` 再应用。用户随后澄清只禁止纯进度提交，已验收实现应正常提交；
保存完整含新文件增量后，将基线安全对齐到真实提交
`da6dd3f3e1976a7f661264caa3d48847da5b3abe`，其树恰等于原审查基线
`3210b95de19513480e9635af00fe2671ee9493f9`。本票只维护自己的状态及必要实现/文档，
父 PRD、ORCHESTRATION 及后续票由协调任务维护。

### RED → GREEN 与边界

- `LegacyObservationTakeoverTests.SameRevision_HistoricalUnknownCanBeCompleted_AndOldReplayKeepsTheCompletion`
  首次真实 PostgreSQL 失败：同版本恢复原 Observer 被 `The same Fact Revision has different content`
  拒绝。修复后旧输入适配器先统一未知/可靠值，原生核心保持严格全等；重复旧表示不清掉补全，
  不增加 Revision，已知固定属性改变仍冲突。追加覆盖 FOI/Aspect 未知、旧 Target 到明确对象
  表示的完整语义幂等，以及 Result 冲突时补全整批回滚。
- `FactStore`、`ObservationStorage` 与 `FactHttpTests` 先行回归 **119/119 通过**；这些与最终
  全仓测试重叠，不累计计数。旧导入/重放两种先后、迟到旧缓存不扩张已缩短区间、Owner/Stream
  隔离、原生完整性、固定字段、完整 JSON 和准确关系沿既有真实入口复验。
- `LegacyObservationMigrationTests` 新增 **4/4**、全部 `MigrationTests` **38/38** 在真实
  隔离 PostgreSQL 18 通过。两个历史基线各有 20 行覆盖完整旧四元键、跨 Owner/流/家族、
  原行 Id、Revision、完整 JSON/精确数值、Source/AppIdentity、微秒时间、零长度段与 Gap tick。
  Browser 安装 UUID 未被 Runtime Instance 替换；无依据 Collector/FOI/Aspect 保持 null。
- 迁移后重复升级逐行不变；注入后期回填失败连续两次整批回滚，移除故障后同库重试成功。
  独立家族行 Id 冲突连续重试安全停止且保留两行。没有改写任何已发布 migration；现有追加链
  已满足测试，无需额外 schema 变更。真实发现冲突时所需的显式改号/引用对应继续归原发布门禁，
  不以 `ON CONFLICT`、跳过历史迁移或虚构兼容资料消除冲突。

### 兼容退出与 04–06 交接

服务端规则见[迁移映射](../../../docs/architecture/observation-storage-migration.md)，
通用缓存格式矩阵、失败恢复、包版本保护和第一方 fixture 要求见
[缓存兼容验收](../../../docs/architecture/observation-cache-compatibility.md)，
实际消费者与退出条件登记于[兼容台账](../../../docs/architecture/compatibility-debt.md)。
历史格式读取通过不等于真实安装已全部升级；04–06 仍须完成各自 System/Browser/VRChat
专有持久状态与实际生产入口验收。没有执行部署、业务库变更或暂停的完整副本演练。

全局模型/语义文档中仍有实施前的版本及状态描述，已将精确位置通知协调任务，由 09 全链
盘点统一刷新，避免重复修改全局权威；03 所触达的迁移与缓存说明在本票修正。

### 通用缓存与审查修复

- SDK 历史 v1 真实 `SchemaRevision/RecordState` 字段原先被判损坏并丢失 Fact；原生 outbox
  排空后原先写回 schema 1。两个稳定 RED 修复后通过；转换/后续校验失败均保全原件，转换写
  schema 4，版本不降级。v1–v3 pending/dead-letter、完整 JSON/时间/Gap、失败发布及重试通过。
- Runtime v1/v2 实际治理/hash 形状的初始矩阵 **2 FAIL / 6 PASS**；确定性转换后 v1–v8
  全通过，保留 Delivered hash 判等结果、Package/LastKnownGood、进行中身份与原始备份。
  SDK 全套 **59/59**、Hub 缓存/协议/旧包启动及回退定向 **102/102** 通过。
- 首次 Standards 审查无发现。Spec 审查发现历史空 FOI 补全晚于关系校验：高版本可以保留
  原机器 FOI 却接受另一机器的关系。新增真实 PostgreSQL 用例先失败（没有抛冲突）；在旧输入
  边界对补全后关系重新校验，修复后本票接管用例 **5/5** 通过，冲突保留原行/版本/关系。
- 审查基线为真实提交 `da6dd3f`；使用临时索引生成包含全部新文件的固定树审查，未将 02/07/08
  已验收内容重复计入。初审树 `20b0676c7a4e05c6cbfd9919d10143967e8ad6b3`。
- 协调任务明确现场归属：实际安装 Package/content hash 全量盘点、owner 确认的最长离线及
  回退窗口是允许移除兼容/实际发布的门禁，原 `observation-storage` PRD（ready-for-human）承接；
  04–06 承接各自生产入口、专有缓存与安装验证，09 统一核对。继续保留兼容时，这些不是03通用
  实现验收的前置条件；03完成不能替代现场验收或授权删除兼容。


### 最终验证与 lifecycle / friction closeout

- 最终代码固定审查树 `249ee1bed6272e4ef5e35c7a22dc32a2d0541206`，只在通过后追加本段完成记录。
- `dotnet tool restore`、`dotnet restore Heartbeat.slnx` 成功；
  `dotnet build Heartbeat.slnx --no-restore` 最终成功，0 errors。
- 审查修复后的 `dotnet test Heartbeat.slnx --no-build --nologo`：13 个项目
  **1,428/1,428 通过，0 失败、0 跳过**，含服务端真实隔离 PostgreSQL **647**、Hub **332**、SDK **59**。
  首次 1,427 项全仓与所有定向验证均为重叠证据，不累计。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`
  与 `git diff --check` 通过。未变更前端及 Browser TypeScript，本票自动验证覆盖实际 C# 改动与全仓 .NET 回归。
- 可复核日志：`/tmp/heartbeat03-all-reviewed.log`、`heartbeat03-build-reviewed.log`、
  `heartbeat03-style-reviewed.log`；RED/定向日志为 `heartbeat03-semantics-red.log`、
  `heartbeat03-relations-red.log`、`heartbeat03-relations-green.log`、`ticket03-runtime-red.log`、
  `ticket03-sdk-redgreen.log`、`ticket03-sdk-final.log`、`ticket03-hub-final.log`（均在 `/tmp`）。
- Friction 已收口：实际旧字段读取缺口、假损坏 Gap、排空降版本及补全后关系校验在本票修复；
  通用迁移说明/消费者/退出条件同步。全局文档漂移交09，现场安装与最长离线/回退窗口、真实生产
  迁移/资源门禁按上述原发布任务和04–06承接，均未宣称通过或据此删除兼容。
- 本票通用实现、自动验证与双轴审查完成，状态 `done`。实际发布/安装与业务库验收仍分别保持原门禁；
  没有部署、推送、业务库修改或恢复暂停的生产副本演练。遵循最新用户规则，仅提交完整验收实现。

## Standards

固定基线 `da6dd3f` 到最终树 `249ee1b`，独立审查无未解决发现；未发现需修复的文档标准违反或
明确维护性 smell。最终补全关系校验与回归符合历史适配边界，兼容文档未把格式 fixture 冒充现场验收。

## Spec

独立初审发现 1 项 P2（补 FOI 后遗漏关系归属校验），已以真实 PostgreSQL RED→GREEN 修复并由
原审查者复核关闭；最终无未解决规格缺失或 scope creep。现场与兼容退出条件有明确承接。

审查合计：Standards 0；Spec 1 已关闭、0 未解决；两轴均无剩余最高风险项。
