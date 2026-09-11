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

## ManagedProcess 专有状态的启动与回退要求（06）

SDK outbox 排空不表示 Collector 专有状态可以降级。写入新版专有状态前，Collector 在同一
Instance data-directory 原子发布 `collector-data-requirements.json`，例如：

```json
{"SchemaVersion":1,"RequiredCapabilities":{"facts.observation":2}}
```

这是持久文件可读性要求，版本值要求候选 Package 明确支持该能力版本，并非“版本号更大即可”。
写端先验证并保留已有要求，再提交要求文件，最后提交专有状态；任何要求写入失败都不得发布
新状态。要求成功但状态提交失败可以保守保留要求。要求不因 ACK、排空、重启或回退降低或删除；
冲突要求、未知信封版本、损坏文件与读取失败均停止并保全，不能当成空要求。

Runtime 在每次 ManagedProcess 启动（含手动启动、更新候选及 LastKnownGood 回退）执行通用检查，
不识别 VRChat 文件名或业务格式。不兼容时返回 `collector_cache_incompatible`，不启动候选进程。
VRChat schema 4 checkpoint 的实际写端与启动门禁经托管测试验证；其旧 v1–v3 专有恢复与现场步骤见
[VRChat README](../../collection/collectors/Heartbeat.Collector.VRChat/README.md)。保留 marker 与整个目录
一起备份/恢复；不得单删 marker 或自动覆盖新状态以恢复旧包。

当前消费者为 VRChat 专有 checkpoint。退出仍需对应目录已迁移/排空或明确归档、实际 Package
版本/content hash 清单及 owner 批准的最长离线/回退窗口；本票保留兼容，不声称这些现场条件已满足。

## 第一方专有格式与现场承接

通用 SDK/Runtime 数字版本不代表各 Collector 的专有缓存版本。04–06 的生产者实现和自动验证已
整合，三票仍待真实安装验收；下表是当前消费者及现场门禁，不是未来才要切换的实现计划。

| 边界 | 真实消费者与当前行为 | 自动验证 / 现场承接 |
| --- | --- | --- |
| System ingress | AppMonitor 的 NDJSON checkpoint、Input first-stage，写 schema2（含排空 reset）。旧记录默认 IsObservation=false，以原 Binding/Stream/FactId、Kind=null 在原 End 正常收尾；新事实为空 Binding、原生 Kind。保留两条真实 Stream 承接旧条目和 Gap | [04](../../.scratch/observation-convergence/issues/04-system-observation-cutover.md) 的生产者 HTTP/重启/容量测试及 `scripts/verify-system-ingress-rollback.py` 实际旧 loader 拒绝、文件集合/SHA-256 保全；owner 按 [System README](../../collection/desktop/Heartbeat.Collector.System/README.md)承接 Windows/macOS 安装、权限和实际回调 |
| System 其他历史缓存 | 通用 SDK outbox/dead-letter、旧 segment/input JSON 仍可能存在于真实 Desktop Profile；沿原身份排空，不把新 Fact 回投旧缓存。当前 System 不使用独立 .NET Segment SDK 状态文件 | 同 04 的旧 JSON/持久保管回归；owner 逐 Profile 记录原目录、schema、未发/未终结/隔离条目及最老时间 |
| Browser storage | 新 journal5 原子保存 fold/pending/dead-letter/Gap。旧 keys 的实际四代布局、一次性备份和读取 fence 以 [Browser 专有台账](../../collection/collectors/Heartbeat.Collector.Browser/cache-compatibility.md)为权威；通用 .NET fixture 不覆盖 Chrome storage | [05](../../.scratch/observation-convergence/issues/05-browser-observation-cutover.md) 的 storage/background/真实 ExternalHost→HTTP 与临时 Chrome 进程重启；owner 按专有台账承接真实 Chrome/Edge 原 Profile 升级 |
| Browser 已 ACK 进行中旧 fold | 扩展旧 keys 可能已无完整快照/版本高水位，Runtime journal 仍保管它。`facts.recover` 在真实 Activation 授权范围内只读精确旧 Stream/FactId；缺失继续保留恢复责任，不猜版本、不离线终结。恢复后按原 Id/版本/完整 Result 收尾和 ACK，当前窗口再开新原生 Fact | 05 的 `BrowserExternalHostRecoveryTests`、真实旧 keys→Runtime恢复→HTTP 保持旧数据库行；退出还需所有受支持 Profile 的旧 fold、Runtime 进行中条目与隔离事实均有归宿 |
| VRChat 专有 checkpoint | 当前 schema4，旧 v1–v3 的 active/pending/Gap 由真实 loader 接管；旧 Kind=null、presence Binding/Stream 保持，未知账号不以当前账号补齐。新事实明确账号 FOI、空 Relations；保留 presence 流用于旧条目与真实 Gap | [06](../../.scratch/observation-convergence/issues/06-vrchat-observation-cutover.md) 的专有矩阵和真实 ManagedProcess→HTTP；owner / Headless 发布维护者按 [VRChat README](../../collection/collectors/Heartbeat.Collector.VRChat/README.md)承接 linux-x64 安装与真实账号授权/离线/重启 |
| ManagedProcess 专有要求文件 | 上节 `collector-data-requirements.json` 随 data-directory 保管，要求在新专有状态前提交；SDK 排空仍禁止不兼容包读取。启动、更新候选、LastKnownGood 都经同一通用门禁 | 06 的启动/回退/IO失败恢复测试；owner / 发布维护者记录各账号目录与精确 Package/content hash，不能单删 marker 作为降级方式 |

Browser 的持久扩展安装 UUID 是实际 CollectorId，Runtime InstanceId/DeliveryInstanceId 仅负责
接收保管；System 的稳定 Instance 是实际 Observer，VRChat 的账号业务身份不能由 Headless 机器补齐。

兼容删除由 owner 与发布维护者在已有 04–06 及 `observation-storage` 发布门禁中承接：记录全量
实际 Package 版本/content hash、Profile/data-directory schema、未发/未终结/隔离数量和最老时间，
确认每项数据归宿并跨过批准的最长离线/回退窗口，再用本矩阵、专有矩阵和现场证据共同审议退出。
停止安装旧程序、当前队列暂空或通用 fixture 通过均不替代这些条件。当前现场清单与窗口未齐，
继续保留兼容；未运行业务库迁移、完整生产副本、资源演练或真实账号操作。
