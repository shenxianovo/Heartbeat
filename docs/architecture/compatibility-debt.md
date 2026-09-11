# Compatibility Debt Ledger

本文记录当前实现仍主动服务的旧数据、旧客户端或旧内部模型。兼容代码本身不自动构成缺陷；
没有明确服务对象、移除门槛和验证方式的兼容分支才会永久化。领域决策仍以 ADR 和各
`CONTEXT.md` 为准，本文只追踪实现债务。

2026-09-11 当前状态：Observations 01–08 的代码已整合，09 正在清理无消费者接口并作最终组合验收。
新观测以自身 Id、Kind、Collector、FOI、Aspect、Result、家族时间和 Revision 独立成立；
新原生输入和显式历史适配汇入同一保存核心及唯一 Facts，不要求 Subject/Stream。
04–06 的实现与自动验收完成，真实安装、权限、账号及完整缓存清单/窗口仍待现场证据。
业务库迁移、发布和暂停的完整副本资源/恢复演练仍由既有 `observation-storage` 门禁承接。

## 现行裁决与权威入口

- 兼容按真实安装与落盘状态退出，不凭提交日期猜窗口。owner 与 Collection/Analytics 发布维护者
  汇总 Desktop、Browser Profile、Headless/Runtime 和备份：精确 Package 版本/content hash、目录
  schema、未发/未终结/隔离数量、最老时间；明确数据归宿并跨过批准的最长离线与回退窗口后才能删除。
- 当前模型见 [ADR-059](../adr/059-observation-storage-five-tables.md)和[观测语义](observation-semantics.md)。
  ADR-054 的 Subject + Fact/Stream 目标、ADR-055 的家族物理分表及旧整行 LegacyRecord 只解释历史；
  当前是一份 Facts/Result，旧上传只服务历史导入/排空，历史 migrations 不改写。
- 通用 SDK/Runtime 历史矩阵、当前格式与包回退要求以[缓存接管](observation-cache-compatibility.md)为权威；
  Browser Chrome storage 的实际四代旧 keys、journal5、fence 和恢复口以[专有台账](../../collection/collectors/Heartbeat.Collector.Browser/cache-compatibility.md)为权威。
  下表描述消费者与退出责任，避免再维护一份可漂移的版本清单。
- Package/Installation/Instance、版本及 Desired State 属于 Runtime；Source 继续承载真实来源、
  observation declaration 与 Matcher 身份。声明是现行消费者，不因旧 Registry 名称而机械删除。
- 严格 HTTP 上行单版本 lockstep 继续成立，服务端先升级；旧客户端 426 不能替代缓存保全和读兼容验收。
  AppIdentity 展示别名是否退出仍需真实客户端矩阵、引用审计与回填证据。

## 保留边界

下表的现场承接者为 owner 与对应 Collection/Analytics 发布维护者；04–06 的专有步骤由现有任务承接，
不另建重复 backlog。未取得全量现场清单与窗口证据前，各行保留读取/恢复能力。

| 边界 | 当前兼容对象 | 主要证据 | 移除门槛 | 移除验证 |
| --- | --- | --- | --- | --- |
| Analytics 历史 Subject 导入 | 升级前 Headless Account/Person 以 `subject:<kind>:<uuid>` 保存的 Device 及历史引用 | `FactStore.LegacyImport.cs`、历史 `NativeFactCustody`、[迁移映射](observation-storage-migration.md) | Analytics owner 核对所有数据库和旧缓存迁移及引用；旧导入 adapter 待受支持缓存与窗口退出，历史事实和基线 migration 保留 | Account/Machine fixture、微秒时间、确定性重放无重复及 Owner 隔离；生产备份演练仍归原存储门禁 |
| 升级前 segment/input 上传缓存 | 实际 Desktop/Headless 的旧 segment/input JSON、重试文件和隔离记录；新 Fact 全部使用 Runtime durable upload | `HeartbeatCacheFormats.cs`、`JsonCacheMigration.cs`、旧 UploadStream composition；09 已删除无人消费的 Input Fact sink/replay/fence 接口 | Collection owner 逐安装证明旧 pending/进行中/隔离记录有归宿并经过批准窗口；Analytics 旧导入端点随后退出；不为已退役投影接口保留假消费者 | 旧缓存与新上传交错、重启、准确确认、迟到旧缓存不覆盖较新事实；实际安装矩阵 |
| 历史事实身份接管 | 旧导入、旧 Runtime 重放和迟到缓存按完整 Owner/Kind/Stream/FactId 对应历史行；保留原行 Id/Revision/Result/时间，历史未知仅在明确适配处恢复 | `FactStore.LegacyImport.cs`、`FactStore` 旧入口、迁移映射与 Ticket03 | Analytics/Collection owner 核对旧导入与重放消费者归零并跨过窗口后移除运行时 adapter；保留事实及已发布 migrations，永久整行 LegacyRecord 不恢复 | 双向到达唯一事实、原生新旧 UUID 碰撞拒绝、未知补全不增版、已知值保护、迁移失败回滚及重试 |
| AppIdentity expand 与展示别名 | 事实表 AppId 双写已删除；`Device.CurrentApp` 旧展示快照、DeviceStatus 的 CurrentApp 别名及旧上传 AppName 仍存在；不把正常产品显示名字段统称为兼容 | `server/Heartbeat.Server/Entities/FactRecord.cs`、`Device.cs`、`shared/Heartbeat.Core/DTOs/` | 所有受支持客户端只消费 AppIdentity/App Key 路径；存量 FK 与查询完成审计和回填 | 数据库 orphan/引用审计、旧客户端 426 演练、新客户端 API/UI 回归 |
| Agent 本地上传缓存迁移 | 无版本旧数组、旧 AppName、旧 input code 形状 | `HeartbeatCacheFormats.cs`、`JsonCacheMigration.cs` | 最低受支持 Agent 版本已经写出当前 schema，且长期离线缓存保留策略已裁决 | 真实旧缓存原子迁移、失败保留备份、重启不重复上传、dead-letter 可见 |
| Collector Runtime/Headless 状态迁移 | `helloAttempts`、`configSchemaVersion`、旧 Instance mapping、无版本 secret envelope | `JsonCollectorRuntimeStore.cs`、`HeadlessFleetOptions.cs`、`EncryptedFileCollectorSecretStoreTests.cs` | 已发布版本与可能存在的本地文件清单明确；所有仍保留的数据已迁移或有恢复方案 | 每个旧 fixture 加载、原子改写、冲突字段拒绝、LKG/Secret 恢复 |
| System ingress 专有恢复 | 实际 AppMonitor/NDJSON checkpoint 与 Input first-stage 旧记录；新记录显式原生，旧条目保持 Binding/Stream/FactId 与 Kind=null；不是独立 .NET Segment SDK 状态文件 | [通用台账中的专有边界](observation-cache-compatibility.md#第一方专有格式与现场承接)、[System README](../../collection/desktop/Heartbeat.Collector.System/README.md)、Ticket04 | owner 完成真实 Windows/macOS 安装/权限回调，核对每个 Profile 的旧条目归宿并结束窗口 | 真实生产者→Runtime→HTTP/PG、crash/restart/Gap、实际旧 loader 拒绝且 SHA-256 不变；平台 fixture 不替代安装 |
| Browser `chrome.storage` 迁移与精确恢复 | 历史 local/session keys、pending/publish-attempt/dead-letter、无已 ACK 高水位的旧 fold、当前备份/fence；真实扩展安装 UUID 继续作 Observer | [Browser 专有台账](../../collection/collectors/Heartbeat.Collector.Browser/cache-compatibility.md)、`facts.recover` 精确旧 Stream/FactId 授权只读口、Ticket05 | Browser owner 完成真实 Chrome/Edge 原 Profile 升级；旧 fold、Runtime 进行中条目及隔离记录均有归宿并经过批准窗口，才可审议旧 keys/fence/恢复口退出 | 四代真实形状 fixture、原 loader 拒绝且文件不改、原子恢复失败重试、完整 JSON/高水位/准确 ACK；临时 Chrome 重启与真实 Profile 安装分别记录 |
| Browser pending Gap 缺少 `gapId` | 已安装 Browser 扩展在 `chrome.storage.local` 的 `heartbeat:delivery:pending-gap` 旧对象/数组；该格式已有 Gap 范围但无 UUIDv7 identity | `delivery-chrome.ts::normalizePendingGaps`；Browser delivery storage fixture 覆盖读取后回写稳定 `gapId` | Collection / Browser owner 盘点所有受支持 Profile，证明每个现存 pending Gap 已被当前扩展成功 load + save 至含 UUIDv7 的当前格式，旧格式盘点归零，并经过明确的最长离线/回滚窗口；盘点和窗口未有现场证据前保留 | 保留缺 `gapId` 的单对象与数组 fixture；验证第一次 load 生成 UUIDv7、save/restart 不再换 identity、Gap 范围/原因/loss count 不变，并记录盘点证据与移除 commit |
| VRChat presence checkpoint 与专有能力要求 | 旧 v1–v3 Active/pending/Gap、SDK与Runtime目录及 LastKnownGood；当前 checkpoint4 先写通用 `collector-data-requirements.json` 要求，不因 SDK 排空降级 | [缓存接管](observation-cache-compatibility.md#managedprocess-专有状态的启动与回退要求06)、[VRChat README](../../collection/collectors/Heartbeat.Collector.VRChat/README.md)、Ticket06 | owner / Headless 发布维护者逐账号记录安装与目录，完成 linux-x64 真实账号验收、旧数据归宿和窗口后审议 reader 退出；marker 随目录保留，不能单删实现回退 | v1–v3 实际恢复/备份/重试，旧未知账号不被接管；真实 ManagedProcess→HTTP/PG、Gap/迟到 ACK；启动/候选/LastKnownGood 共用旧包拒绝 |
| Collector Protocol outbox schema v1 point Gap | 已落盘 `collector-protocol-outbox.json` 中由旧 InputEvent eviction 产生的 `Start == End` Gap；当前协议要求非空范围 | `CollectorProtocolOutbox.MigrateCurrentPointGaps`；当前 fixture 只已证明保留后续 Fact、reason 不变、End 精确 +1 tick 且不 quarantine；其余项是移除前待补证据 | Collection / Protocol owner 盘点所有受支持 Desktop/Headless Collector data directory，证明当前 binary 至少成功 Open + persist 每个现存 point Gap outbox，盘点归零并经过明确离线/回滚窗口；未有现场证据前不以版本日期删除 | 保留 schema v1 point-Gap fixture；验证 MessageId/GapId/BindingId/Start/reason/loss count 不变、End 精确 +1 tick、重启不再重写、失败仍保留 durable outbox；移除时记录盘点和 commit |
| Collector Protocol outbox schema v1 无跨类顺序 | 已落盘 outbox 只有 `Facts`/`Gaps` 两张 list，没有 `DeliveryOrder`；旧 binary 的可观察语义是 Facts 全部先于 Gaps | `RestoreLegacyDeliveryOrder`；`RestartPreservesInterleavedFactGapFactDeliveryOrder` 证明当前写格式在 restart 后保留 Fact→Gap→Fact；旧 fixture 仅按 Facts→Gaps 恢复旧行为 | Collection / Protocol owner 盘点所有受支持 data directory，证明无 `DeliveryOrder` 的现存 outbox 已被当前 binary 成功 Open + persist，盘点归零并经过明确离线/回滚窗口 | 保留无 order 的 Fact+Gap fixture 证明不 quarantine 且仍 Facts→Gaps；保留当前交错 fixture 证明 restart/retry/rekey/容量驱逐不改变 order；移除时记录盘点与 commit |
| Collector Runtime state v2 Gap 缺少 `GapId` | 旧 Hub `collector-runtime.json` 的完整 Gap 范围/原因/loss 未带身份 | 原生上传迁到 state v3 前保留 `.v2.bak`，持久分配一次 GapId；`AwaitingLegacyGapIdentity` 仅允许同 Stream/范围/原因/loss 全等的首次 lost-ACK 重试绑定旧身份别名，保持已上传身份不变 | 所有实际 Hub 状态与 Collector outbox 完成升级/重放盘点且回滚窗口明确；未完成前保留 v2 reader 与精确别名恢复 | 缺Id迁移后重启保持身份；原始 Gap lost-ACK 重放只绑定一次别名不重复上行，冲突不修改；备份与失败重试 fixture |
| source 声明持久接口 | Desktop 历史配置仍保存 source 的 declaration/version，Runtime 经 `ICollectorDeclarationStore` 使用包的验证声明；Registry 的 Touch/Discover 已无消费者并由09删除 | `ICollectorRegistry.cs`、Windows/Mac configuration adapters、Hub composition | Collection owner 确认历史配置迁移后可进一步收缩 Registry 包装；Source 深度声明与 Matcher 是现行契约，不作为兼容债务删除；Package/Instance/启用/准入不回到 source Registry | 依赖搜索只保留声明 seam、包声明覆盖/版本与重启回归；无 Touch/Discover 活跃调用 |
| 严格上行协议切换 | 旧 Agent 收到 426，新 Agent 迁移缓存后重传 | `RequireHeartbeatProtocolAttribute.cs`、`SegmentIngestContract.cs`、`UploadStream.cs` | 完成真实旧客户端升级演练，并明确协议版本支持/弃用窗口 | server-first 演练、426 UI、缓存容量、迁移后幂等重传与坏记录隔离 |
| Browser 旧 Observer/应用上下文归属 | 旧 Runtime、扩展快照和历史数据库仍可能带 Observer/Target 与 application-context；新 Browser 已是 App FOI 和精确 observed-on 关系 | [迁移映射](observation-storage-migration.md)、[通用缓存](observation-cache-compatibility.md)、Browser 专有台账、Tickets03/05 | owner 盘点旧 Profile/Runtime/数据库和备份，完成确定性转换及重放后跨过窗口；未知历史保持可读，不补造设备或账号依据 | 历史归属矩阵、安装 UUID、App 维护重放、完整 ACK；当前新事实不经旧 Target/应用上下文转换 |
| Fact 缺 Aspect 的旧契约 | 历史 Runtime/SDK/Browser 快照、旧 `/facts` 和 segment/input 上传、数据库空 Aspect；新 `/observations` 不接受缺 Aspect | `FactAspectCompatibility`、`ExplicitFactAspects` migration、[语义边界](observation-semantics.md)及缓存权威矩阵 | Collection/Analytics owner 证明旧形状流量/缓存归零、历史数据归宿明确并结束窗口；不能因 SQL 历史 null 能力放宽新原生输入 | 保留实际历史 fixture，Id/Revision/时间/Result/Delivered 不变；未知显式 Aspect 不被猜义；移除 Infer/SQL fallback 前证明无消费者，历史 migration 保留 |
| SDK / Runtime 新缓存的旧包回退 | 旧 outbox/dead-letter/Runtime 状态和可启动/回退 Package；原生格式与旧能力兼容按通用矩阵读取 | [缓存接管](observation-cache-compatibility.md)、ManagedProcess 启动/候选/LastKnownGood 共用检查 | Collection/Protocol owner 盘点受支持 Package 和数据目录及允许窗口；拒绝保护不能以队列排空、删除能力文件或回灌备份绕过 | 手动/自动启动入口一致，拒绝时原件不变、不造损坏 Gap，兼容包恢复与准确 ACK |
| Fact 旧 Observer/Target envelope | 历史 Runtime/SDK、Browser 旧 keys 和旧 HTTP 请求；当前新 Fact 直接携带 Collector/FOI/Relations | `ObservationCompatibility`、[通用矩阵](observation-cache-compatibility.md)、Browser 专有矩阵 | Collection/Analytics owner 完成所有受支持安装和备份转换、旧形状流量/待发归零且跨过窗口 | 原身份/Revision/时间/Result/Delivered 保全、同版冲突、失败保留、旧包拒绝；历史 migrations 不改写 |
| Result 的读取别名与历史展示回落 | 原始 Fact HTTP 的 payload/result 指向同一内容；现有前端/生成客户端仍支持 payload，历史无 Collector 的窗口分组可使用非 legacy-import Stream | FactResponse、generated client、Person Fact View、`segmentAdapters`；Tickets01/07/08 | Analytics/Dashboard owner 完成实际客户端迁移后审议 payload alias；历史分组回落需保留准确 Collector 或明确接受无窗口身份装箱，不能丢历史回放 | 任意 JSON 往返无损，input 两别名遵守展示边界，未知 Aspect 原样显示；旧回放与原生多窗口组件回归 |

## 维护规则

- 新增兼容分支时在同一改动中补一行，或者链接到已有行。
- 移除兼容代码前先保留对应 fixture；移除后把本行改为已退役记录或在 ADR/issue 中留下结果。
- “个人部署只有一个用户”可以缩短支持窗口，但仍需根据真实本地文件和已安装客户端裁决，不能
  仅按代码提交日期猜测。

### 独立观测交付（Observations Ticket 02，2026-09-11）

- 当前通用接口以 `Kind != null` 显式选择独立事实，`CollectorFact`、`BoundCollectorFact`、
  `FactSubmission` 和协议 wire 的 `Payload/payload` 是完整 Result 的兼容名称；不规范化未知原生结果。
  HTTP `ObservationSnapshot` 只使用 `Result/result`。Runtime journal 的 `Payload` 同样是一份原始结果，
  没有新旧双写或第二份活动存储；`DeliveryInstanceId` 仅记录 Runtime 本地保管归属，
  不等同具体 Observer 的 `CollectorId`；本地 `IsFinal/ObservedAt` 用于终态和准确 ACK，不进入 HTTP 事实契约。
- `Kind == null` 的旧协议输入在 Runtime 明确适配；历史 SDK schema 1–3、Runtime schema 1–8 继续读取，
  当前原生 outbox/dead-letter 为 schema 4、Runtime 为 schema 9。新缓存必须由理解 `facts.observation: 2`
  的包处理；InProcess 与 ManagedProcess 初始化/回退前检查缓存，ExternalHost 协商拒绝原生 v1 发布。
- System、Browser、VRChat 的实际新生产入口已在04–06切换。继续使用旧协议的消费者是升级前
  进行中/待发事实、旧 SDK/outbox、Runtime journal、旧 `/api/v1/facts` 和历史导入缓存。
  Ticket03 的通用矩阵与04–06专有矩阵已验证读取能力；所有真实安装清单与最长离线/回退窗口仍未齐。
  旧事实保持 Kind=null 与原 Binding/Stream/FactId，直到正常终结及准确 ACK；不能为删除 adapter 改换身份。
  owner 按上表及原发布门禁确认数据归宿和窗口退出后，再以三执行方式、缓存重启、Gap、真实 HTTP/
  原始读取和迟到 ACK 回归审议删除。
- 无交付 Binding 的原生 SDK outbox 满时背压并保留既有数据，调用方保留尚未接纳的观测以重试。
  有实际 Binding 的容量丢失继续形成稳定 Gap；分组不决定原生 Fact Id、FOI 或 Kind。

### 历史事实及通用缓存接管（Observations Ticket 03）

- 服务端旧输入只经明确 `/facts` / import 边界恢复语义与完整旧身份；同版本历史未知补全
  不增加 Revision，不允许改已知固定属性，旧表示重放不抹掉可靠值。原生 `/observations`
  的必填及不变量不因历史空值放宽。保留消费者包括旧 Runtime journal、旧 SDK outbox、
  Desktop 活动/输入缓存和第一方进行中事实，直到正常终结及准确 ACK。
- 支持数据库起点、真实 PostgreSQL 逐行迁移/冲突回滚/重试证据及明确停止条件见
  [迁移映射](observation-storage-migration.md#ticket-03历史重放与通用验证边界2026-09-11)。
  现有追加链保持不变；不能以追加 migration 越过前置身份冲突。
- Runtime 1–8、SDK 1–3 的实际落盘字段与当前 9/4 的验证矩阵，以及 04–06 的专有状态
  实际格式及现场承接见[缓存兼容验收](observation-cache-compatibility.md)。这个矩阵证明读取能力，
  不是所有真实安装已经升级的证明。旧 SDK 在新版本排空后仍不得消费被降版的状态，
  转换失败保留原文件和重试依据，不产生虚构 Gap。
- 删除门槛仍由 Collection/Analytics owner 汇总实际 Desktop、Browser Profile、VRChat、
  Headless 安装与备份，证明旧形状待发归零，确认并结束最长离线及回退窗口；04–06
  和原发布/生产副本任务分别承接现场验证。未形成现场证据前保留兼容 reader 和 fixtures。
