# Compatibility Debt Ledger

本文记录当前实现仍主动服务的旧数据、旧客户端或旧内部模型。兼容代码本身不自动构成缺陷；
没有明确服务对象、移除门槛和验证方式的兼容分支才会永久化。领域决策仍以 ADR 和各
`CONTEXT.md` 为准，本文只追踪实现债务。

## Owner rulings（2026-08-28）

- 兼容支持按**真实安装与落盘状态**退出，不凭提交日期猜窗口：所有 Desktop 安装、Browser
  Profile 与 Headless/Runtime 状态分别完成当前版本迁移，并保留 fixture/backup 验证后，才删除
  对应迁移代码。
- Analytics 的目标边界是原生 **Subject + Fact/Stream ingest**。ADR-054 已把旧上传回投限定为
  升级期导入；ActivitySegment/InputEvent 由 Fact Store 同事务维护为查询投影，不再拥有事实写权。
- AppIdentity 双写与 response alias 计划删除；门槛是实际客户端矩阵、FK 回填与 orphan/引用审计，
  不能用“旧上传已收到 426”替代读兼容证据。
- Package/Installation/Instance identity、版本与 Desired State 归 Collector Runtime；旧 source
  registry 最终只保留明确需要的 observation declaration seam，Enabled/准入消费者迁完后删除。
- 严格上行协议当前保持单版本 lockstep；支持窗口同样由实际安装升级与缓存迁移演练决定。

| 边界 | 当前兼容对象 | 主要证据 | 移除门槛 | 移除验证 |
| --- | --- | --- | --- | --- |
| Analytics 历史 Subject 导入 | 升级前 Headless Account/Person 以 `subject:<kind>:<uuid>` 保存的 Device 及其历史引用 | ADR-054、`FactStore.LegacyImport.cs`、`NativeFactCustody` migration | 原生 Fact Subject 已成为写入边界；所有真实数据库及旧缓存完成迁移并审计后，移除运行时旧导入 adapter；迁移与原始档案保留 | Account/Machine migration fixture、旧 InputEvent 仅按历史微秒精度接管（原生时间保留 tick）、原生重放无重复、Owner/Subject 查询隔离、真实备份演练 |
| 升级前 segment/input 上传缓存与投影 harness | 现存 `segments-cache.json`、InputEventBuffer/重试文件及旧嵌入式 Runtime protocol test harness；生产新 Fact 使用 Runtime durable upload | ADR-054、`CollectorRuntime.Upload.cs`、`RuntimeFactUploadSource.cs`、旧 `UploadStream` composition | 所有已安装 Desktop/Headless 原生上传 smoke 完成，旧缓存 pending 为零且备份/回滚窗口明确；protocol fixtures 迁至 native custody 后移除 `EnableFactUpload=false` 与旧 projector seam；Analytics 旧端点同时退出 | 旧缓存+原生重放交错、精确确认、断网重启、撤回后迟到旧缓存不复活、实际安装矩阵 |
| Analytics 历史 Fact 来源档案 | 迁移前 ActivitySegment/InputEvent 不含 Stream/Revision 的原始记录；LegacyImport 明确记录不可恢复的元数据 | `ObservedFact.LegacyRecord`、`FactStore.LegacyImport.cs`、ADR-054 | 原始档案属于用户历史保留要求，不按兼容窗口自动删除；旧导入写入口仅在实际缓存归零后移除 | 迁移前后行数、时间范围、URL/原始属性与输入计数逐项核对；确定性关联原生事实后档案仍保留 |
| AppIdentity expand 双写与 DTO 别名 | `ActivitySegment.AppId`、`Device.CurrentApp`、`AppName`/DisplayName 兼容属性 | `server/Heartbeat.Server/Entities/ActivitySegment.cs`、`Device.cs`、`shared/Heartbeat.Core/DTOs/` | 所有受支持客户端只消费 AppIdentity/App Key 路径；存量 FK 与查询完成审计和回填 | 数据库 orphan/引用审计、旧客户端 426 演练、新客户端 API/UI 回归 |
| Agent 本地上传缓存迁移 | 无版本旧数组、旧 AppName、旧 input code 形状 | `HeartbeatCacheFormats.cs`、`JsonCacheMigration.cs` | 最低受支持 Agent 版本已经写出当前 schema，且长期离线缓存保留策略已裁决 | 真实旧缓存原子迁移、失败保留备份、重启不重复上传、dead-letter 可见 |
| Collector Runtime/Headless 状态迁移 | `helloAttempts`、`configSchemaVersion`、旧 Instance mapping、无版本 secret envelope | `JsonCollectorRuntimeStore.cs`、`HeadlessFleetOptions.cs`、`EncryptedFileCollectorSecretStoreTests.cs` | 已发布版本与可能存在的本地文件清单明确；所有仍保留的数据已迁移或有恢复方案 | 每个旧 fixture 加载、原子改写、冲突字段拒绝、LKG/Secret 恢复 |
| Browser `chrome.storage` 迁移 | 旧 pending segment、policy/config key 与 `appName` 字段 | `collection/collectors/Heartbeat.Collector.Browser/src/delivery-chrome.ts` | 明确扩展最低支持版本和最长离线升级窗口；确认旧 storage 不再需要直升当前版 | Chrome storage fixture、Service Worker 重启、outbox/FactId 保留、无旧 transport fallback |
| Browser pending Gap 缺少 `gapId` | 已安装 Browser 扩展在 `chrome.storage.local` 的 `heartbeat:delivery:pending-gap` 旧对象/数组；该格式已有 Gap 范围但无 UUIDv7 identity | `delivery-chrome.ts::normalizePendingGaps`；Browser delivery storage fixture 覆盖读取后回写稳定 `gapId` | Collection / Browser owner 盘点所有受支持 Profile，证明每个现存 pending Gap 已被当前扩展成功 load + save 至含 UUIDv7 的当前格式，旧格式盘点归零，并经过明确的最长离线/回滚窗口；盘点和窗口未有现场证据前保留 | 保留缺 `gapId` 的单对象与数组 fixture；验证第一次 load 生成 UUIDv7、save/restart 不再换 identity、Gap 范围/原因/loss count 不变，并记录盘点证据与移除 commit |
| VRChat presence checkpoint v1 读取 | 已落盘的 `presence.json` schema v1：只含未 final 的 `Active`；schema v2 是当前写格式并增加 `PendingFacts`/`PendingGaps` | `VRChatPresenceCheckpoint.Open` 的显式 `1 or 2` 分支；`CurrentV1CheckpointLoadsWithoutShrinkingAndRewritesAtomicallyAsV2` fixture 证明旧 FactId/Start/End 不 shrink，下一次 Stage 原子写为 v2 | Collection / VRChat owner 盘点所有仍受支持的 VRChat Collector 数据目录；每个现存 v1 文件都在当前 binary 下至少完成一次成功 Stage 持久化，盘点结果为零个 v1，并经过一个明确的 rollback/离线保留窗口。部署清单与窗口尚未形成证据前不得删除读取分支 | 保留 v1 fixture；对盘点样本执行 v1 load → v2 Stage → restart，验证 FactId/Start/End、pending replay 与 corrupt quarantine；删除分支时跑 VRChat checkpoint suite，并在本台账记录盘点证据、窗口和移除 commit |
| Collector Protocol outbox schema v1 point Gap | 已落盘 `collector-protocol-outbox.json` 中由旧 InputEvent eviction 产生的 `Start == End` Gap；当前协议要求非空范围 | `CollectorProtocolOutbox.MigrateCurrentPointGaps`；当前 fixture 只已证明保留后续 Fact、reason 不变、End 精确 +1 tick 且不 quarantine；其余项是移除前待补证据 | Collection / Protocol owner 盘点所有受支持 Desktop/Headless Collector data directory，证明当前 binary 至少成功 Open + persist 每个现存 point Gap outbox，盘点归零并经过明确离线/回滚窗口；未有现场证据前不以版本日期删除 | 保留 schema v1 point-Gap fixture；验证 MessageId/GapId/BindingId/Start/reason/loss count 不变、End 精确 +1 tick、重启不再重写、失败仍保留 durable outbox；移除时记录盘点和 commit |
| Collector Protocol outbox schema v1 无跨类顺序 | 已落盘 outbox 只有 `Facts`/`Gaps` 两张 list，没有 `DeliveryOrder`；旧 binary 的可观察语义是 Facts 全部先于 Gaps | `RestoreLegacyDeliveryOrder`；`RestartPreservesInterleavedFactGapFactDeliveryOrder` 证明当前写格式在 restart 后保留 Fact→Gap→Fact；旧 fixture 仅按 Facts→Gaps 恢复旧行为 | Collection / Protocol owner 盘点所有受支持 data directory，证明无 `DeliveryOrder` 的现存 outbox 已被当前 binary 成功 Open + persist，盘点归零并经过明确离线/回滚窗口 | 保留无 order 的 Fact+Gap fixture 证明不 quarantine 且仍 Facts→Gaps；保留当前交错 fixture 证明 restart/retry/rekey/容量驱逐不改变 order；移除时记录盘点与 commit |
| Collector Runtime state v2 Gap 缺少 `GapId` | 旧 Hub `collector-runtime.json` 的完整 Gap 范围/原因/loss 未带身份 | 原生上传迁到 state v3 前保留 `.v2.bak`，持久分配一次 GapId；`AwaitingLegacyGapIdentity` 仅允许同 Stream/范围/原因/loss 全等的首次 lost-ACK 重试绑定旧身份别名，保持已上传身份不变 | 所有实际 Hub 状态与 Collector outbox 完成升级/重放盘点且回滚窗口明确；未完成前保留 v2 reader 与精确别名恢复 | 缺Id迁移后重启保持身份；原始 Gap lost-ACK 重放只绑定一次别名不重复上行，冲突不修改；备份与失败重试 fixture |
| 历史 source 级 Collector Registry | 旧配置、声明缓存与 source 级读模型 | `collection/hub/Heartbeat.Collection.Hub/Collectors/ICollectorRegistry.cs`、`collection/CONTEXT.md` | Package/Instance/Runtime State 与声明 seam 覆盖所有实际消费者，UI/准入不再读取 Registry 身份 | 依赖搜索为零或只剩明确声明 seam；browser/system/状态 UI 回归 |
| 严格上行协议切换 | 旧 Agent 收到 426，新 Agent 迁移缓存后重传 | `RequireHeartbeatProtocolAttribute.cs`、`SegmentIngestContract.cs`、`UploadStream.cs` | 完成真实旧客户端升级演练，并明确协议版本支持/弃用窗口 | server-first 演练、426 UI、缓存容量、迁移后幂等重传与坏记录隔离 |

## 维护规则

- 新增兼容分支时在同一改动中补一行，或者链接到已有行。
- 移除兼容代码前先保留对应 fixture；移除后把本行改为已退役记录或在 ADR/issue 中留下结果。
- “个人部署只有一个用户”可以缩短支持窗口，但仍需根据真实本地文件和已安装客户端裁决，不能
  仅按代码提交日期猜测。
