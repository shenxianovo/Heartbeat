# 通用 SDK / Runtime 缓存接管

2026-09-11，Observations Ticket 03。这里记录通用持久保管格式；System、Browser、VRChat 的现场
运行状态与升级验收分别由 Tickets 04–06 承接。当前通用 Runtime 写 schema 9，SDK 独立观测写
schema 4，能力为 `facts.observation:2`。文件版本不是 Collector Package 版本。

## 支持的历史形状

下列 JSON fixtures 按 Git 中实际历史 DTO 与 serializer 设置构造，使用虚构且稳定的 UUID、时间、
配置与结果，经过真正的文件加载、迁移、重启与 ACK 接口验证。它们不是从业务安装导出的现场副本，
不能据此声称已完成所有已安装 Package 版本、最长离线时间或回退窗口的盘点。

| 缓存 | 历史定义依据 | 转换与保留 |
| --- | --- | --- |
| Runtime v1 | `d7da02f` / `e85c0a4` 之前的命名兼容边界 | `configSchemaVersion`、LastKnownGood 配置版本与 `helloAttempts` 改为当前字段；Package 内容 hash、配置 JSON、Instance/Subject/Stream 身份原样保留 |
| Runtime v1–v2 早期形状 | `e85c0a4`、`31216d4`、`ff3a152` 前后的 DTO | 移除已退役 Schema 描述和 ContentHash 机械字段；仅 `recordState=present` 可转换；`deliveredContentHash == contentHash` 且非空时保留 Delivered=true，其余继续待发；旧文件备份保留全部原元数据 |
| Runtime v3 | `ff3a152` | 已有布尔 Delivered；只按明确旧 Source/Subject 兼容规则恢复可靠归属 |
| Runtime v4 | `300c64a` | System Observer/Target；不从新格式中缺失的字段重新猜测身份 |
| Runtime v5 | `985acaa` | Browser 已有扩展 Observer/应用上下文；旧 identityKey 按原活动兼容规则转换 |
| Runtime v6 | `996264b` | 账号归属及 activityKey；保留未知结果字段 |
| Runtime v7 | `05e892a` | Aspect 与 Observer/Target；转换为 CollectorId、FOI、Relations |
| Runtime v8 | `6526e13` | 已有直接对象信封，保持 CollectorId/FOI/Relations/Aspect 及未知值；升级外壳为 v9 |
| SDK v1 | `cfa3e25`、`ff3a152`、`7c5775e` | 包含有/无 SchemaRevision、数字 RecordState 的历史代际；缺 DeliveryOrder 时沿旧事实先于 Gap 顺序恢复；不把旧事实的 BindingId/FactId 改为原生身份 |
| SDK v2 | `05e892a` | Aspect 和可选 Observer/Target，转换旧归属信封 |
| SDK v3 | `6526e13` | 直接 CollectorId/FOI/Relations，尚无独立 Kind/Source；读取不赋予新原生含义 |

Runtime fixtures 位于 `collection/hub/Heartbeat.Collection.Hub.Tests/Fixtures/CacheCompatibility/`，
SDK fixtures 位于 `collection/protocol/Heartbeat.Collection.CollectorProtocol.Tests/Fixtures/CacheCompatibility/`。
同一数字版本内曾退役字段而未立即改版本号，因此不能仅把当前序列化输出改成旧版本号来代替历史 fixture。
早期 schema 1 / 2 与后期同版本的字段缺省形状都由读取兼容承接；额外未知信封字段仍拒绝，结果 JSON 内
未知字段完整保管。

## 接管、失败与回退边界

- Runtime 先转换、验证，再保留 `.vN.bak` 并原子发布经重载验证的 schema 9 文件。失败保留原文件；
  重试不重分配 FactId、Revision 或 Stream。旧 Gap 缺身份时由既有一次性持久身份/alias 流程接管。
- SDK 对可识别的旧 JSON 信封，转换或后续校验失败均停止读取并保留原文件；不生成 `outbox_corrupted`
  Gap。只有确实不可解析的损坏字节继续沿原有隔离恢复规则处理，原始损坏证据先保全。
- SDK 真正执行历史字段转换时保留原 `.vN.bak`，转换结果写 schema 4，防止旧包消费已经删除旧必要
  字段的状态。没有转换、也没有原生字段的旧调用者仍可保存最低可表达版本；一旦写入 schema 4，
  即使队列排空也不降低版本。备份仅供明确恢复，不会在失败或重启时自动回滚覆盖较新事实。
- outbox/dead-letter 均保留完整 Fact 时间、ObservedAt、Revision、结果、错误证据与投递身份；迁移不
  自动重传 dead-letter。SDK 只有 pending 状态，已 ACK 条目从 outbox 移除；Runtime 的 Delivered 是
  Analytics 已准确确认当前快照的独立事实。
- 旧事实保持 `Kind=null` 与原 Binding/Stream 身份。Runtime journal 中原生 `DeliveryInstanceId` 仅
  表示交付保管者，绝不能覆盖实际观测者 CollectorId，尤其不能代替 Browser 扩展安装 UUID。
- 已交付但尚未终结的 Segment 保留在 Runtime journal；准确 ACK 包含完整快照及本地 IsFinal/ObservedAt，
  不能确认同 Revision 下不同结果、时间或归属，也不能确认后来到达的更高修订。
- ManagedProcess 的启动和自动回退在运行旧包前检查 outbox/dead-letter 版本及 Package 能力。schema 4
  要求 `facts.observation:2`，schema 3 要求 `facts.observation:1`，schema 2 要求 `facts.aspect:1`。
  Runtime 旧二进制读取 schema 9 会失败；禁止用备份自动覆盖新状态来实现降级。
- 历史撤回状态没有安全的“变成当前事实”转换。通用加载明确拒绝并保全原件，等待单独恢复决策；不凭
  该记录制造 Gap，也不默默复活。本票不恢复已退役的生产撤回协议。

## 验证与退出门槛

本票先稳定复现 SDK v1 原始字段被当作损坏（Fact 丢失）以及原生 outbox 排空后降到 schema 1，
再修复，最初 2 FAIL → 2 PASS。Runtime 真实 v1–v8 DTO 矩阵最初 2 FAIL / 6 PASS（v1/v2 被旧治理字段
阻塞），修复后 8 PASS。最终 SDK 全套 59 PASS；Hub 缓存、InProcess transcript 及 ManagedProcess
旧包启动门禁定向 102 PASS，包含 Runtime 升级失败保全/重试与不可转换旧记录不复活。

```sh
dotnet test collection/protocol/Heartbeat.Collection.CollectorProtocol.Tests --no-restore
dotnet test collection/hub/Heartbeat.Collection.Hub.Tests --no-restore --filter 'FullyQualifiedName~CollectorRuntimeCacheCompatibilityTests|FullyQualifiedName~OldPackageCannotOpenObservationCacheAfterRuntimeRestart|FullyQualifiedName~InProcessCollectorProtocolTranscriptTests'
```

兼容消费者是仍持有对应 JSON 文件的 Desktop/Headless Runtime、SDK Collector 数据目录及隔离记录恢复。
停止安装旧程序不等于这些文件已排空。删除任何分支前必须记录实际安装 Package 版本/content hash、数据
目录 schema、未发/未终结/隔离数量、最老待发时间，以及明确的最长离线与可回退窗口；所有受支持目录
已迁移且待发/进行中/隔离记录均有明确归宿，并跨过批准窗口后，才能以本 fixture 矩阵加现场证据确认
退出。当前未获取该现场清单，未确认窗口长度；因此保留兼容，人工门禁仍在。

## Tickets 04–06 的交接

- **04 System**：读取真实安装中的 Segment SDK 恢复状态、Input first-stage 与旧 segment/input JSON，
  验证进行中桌面活动沿原 FactId/Revision 终结；已 ACK 但进行中的事实在容量压力下仍可继续；前台/标题/
  away 切换与输入 Gap 使用原始时间。通用入口是 SDK `CollectorFact`、Runtime `ReadPendingFacts` /
  `ConfirmUploadedFacts`；不得把新观测回投到旧缓存。
- **05 Browser**：提供扩展安装 UUID、旧 storage key、pending/delivered/dead-letter、开放窗口和重启后
  仍存在/已关闭窗口的真实快照，验证 UUID、windowId 与并行事实身份。浏览器专有 storage 的 v1–v4 与
  Service Worker 恢复由该票执行；不要用本文 .NET outbox fixture 代替。原生事实 CollectorId 始终为
  真实扩展安装 UUID，Runtime InstanceId 只负责接收保管。
- **06 VRChat**：提供账号级 Package/data-directory、独立账号恢复状态、LastKnownGood Package 与
  断网期间位置事实；验证两个账号/Instance 的身份与离线修订不互串，旧包回退被通用门禁拒绝时原目录
  保留，恢复兼容包后继续准确交付。服务账号业务身份不能由 Headless 宿主机器补齐。

各票需记录实际安装版本与失败/离线/回退时间窗口。本票的通用 fixture 不宣称这些现场验收完成；
未运行业务库迁移、完整生产副本、资源演练或真实账号操作。
