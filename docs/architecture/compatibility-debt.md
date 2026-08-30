# Compatibility Debt Ledger

本文记录当前实现仍主动服务的旧数据、旧客户端或旧内部模型。兼容代码本身不自动构成缺陷；
没有明确服务对象、移除门槛和验证方式的兼容分支才会永久化。领域决策仍以 ADR 和各
`CONTEXT.md` 为准，本文只追踪实现债务。

## Owner rulings（2026-08-28）

- 兼容支持按**真实安装与落盘状态**退出，不凭提交日期猜窗口：所有 Desktop 安装、Browser
  Profile 与 Headless/Runtime 状态分别完成当前版本迁移，并保留 fixture/backup 验证后，才删除
  对应迁移代码。
- Analytics 的目标边界是原生 **Subject + Fact/Stream ingest**；当前 Device 与
  ActivitySegment/InputEvent 回投是待迁移兼容层，不提升为永久领域接口。
- AppIdentity 双写与 response alias 计划删除；门槛是实际客户端矩阵、FK 回填与 orphan/引用审计，
  不能用“旧上传已收到 426”替代读兼容证据。
- Package/Installation/Instance identity、版本与 Desired State 归 Collector Runtime；旧 source
  registry 最终只保留明确需要的 observation declaration seam，Enabled/准入消费者迁完后删除。
- 严格上行协议当前保持单版本 lockstep；支持窗口同样由实际安装升级与缓存迁移演练决定。

| 边界 | 当前兼容对象 | 主要证据 | 移除门槛 | 移除验证 |
| --- | --- | --- | --- | --- |
| Analytics 的 Subject 仍投影为 Device | Headless Account Subject 通过 `FixedSubjectIdentity` 进入现有 Device/Segment API | `collection/hub/Heartbeat.Collection.Headless/HeadlessInstancePipelines.cs`、`server/Heartbeat.Server/Entities/ActivitySegment.cs` | Analytics 建立独立 Subject 身份、完成存量迁移，并让查询/鉴权不再依赖 `DeviceId` | Account 与 Machine 数据迁移回放；报表、Replay、权限及多 Instance E2E |
| Collector Fact 回投旧上传模型 | Segment Fact → `ActivitySegmentItem` 缓冲；Event Fact → InputEvent upload buffer | `SegmentIngestService.cs`、`InputEventBuffer.cs`、`HeadlessInstancePipelines.cs` | Analytics 拥有原生 Fact/Stream 摄入或明确决定旧表就是稳定投影边界 | ACK/重放/乱序、离线缓存迁移、服务器快照 + 新客户端 data smoke |
| AppIdentity expand 双写与 DTO 别名 | `ActivitySegment.AppId`、`Device.CurrentApp`、`AppName`/DisplayName 兼容属性 | `server/Heartbeat.Server/Entities/ActivitySegment.cs`、`Device.cs`、`shared/Heartbeat.Core/DTOs/` | 所有受支持客户端只消费 AppIdentity/App Key 路径；存量 FK 与查询完成审计和回填 | 数据库 orphan/引用审计、旧客户端 426 演练、新客户端 API/UI 回归 |
| Agent 本地上传缓存迁移 | 无版本旧数组、旧 AppName、旧 input code 形状 | `HeartbeatCacheFormats.cs`、`JsonCacheMigration.cs` | 最低受支持 Agent 版本已经写出当前 schema，且长期离线缓存保留策略已裁决 | 真实旧缓存原子迁移、失败保留备份、重启不重复上传、dead-letter 可见 |
| Collector Runtime/Headless 状态迁移 | `helloAttempts`、`configSchemaVersion`、旧 Instance mapping、无版本 secret envelope | `JsonCollectorRuntimeStore.cs`、`HeadlessFleetOptions.cs`、`HeadlessFleetManager.cs`、`EncryptedFileCollectorSecretStoreTests.cs` | 已发布版本与可能存在的本地文件清单明确；所有仍保留的数据已迁移或有恢复方案 | 每个旧 fixture 加载、原子改写、冲突字段拒绝、LKG/Secret 恢复 |
| Browser `chrome.storage` 迁移 | 旧 pending segment、policy/config key 与 `appName` 字段 | `collection/collectors/Heartbeat.Collector.Browser/src/delivery-chrome.ts` | 明确扩展最低支持版本和最长离线升级窗口；确认旧 storage 不再需要直升当前版 | Chrome storage fixture、Service Worker 重启、outbox/FactId 保留、无旧 transport fallback |
| VRChat presence checkpoint v1 读取 | 已落盘的 `presence.json` schema v1：只含未 final 的 `Active`；schema v2 是当前写格式并增加 `PendingFacts`/`PendingGaps` | `VRChatPresenceCheckpoint.Open` 的显式 `1 or 2` 分支；`CurrentV1CheckpointLoadsWithoutShrinkingAndRewritesAtomicallyAsV2` fixture 证明旧 FactId/Start/End 不 shrink，下一次 Stage 原子写为 v2 | Collection / VRChat owner 盘点所有仍受支持的 VRChat Collector 数据目录；每个现存 v1 文件都在当前 binary 下至少完成一次成功 Stage 持久化，盘点结果为零个 v1，并经过一个明确的 rollback/离线保留窗口。部署清单与窗口尚未形成证据前不得删除读取分支 | 保留 v1 fixture；对盘点样本执行 v1 load → v2 Stage → restart，验证 FactId/Start/End、pending replay 与 corrupt quarantine；删除分支时跑 VRChat checkpoint suite，并在本台账记录盘点证据、窗口和移除 commit |
| 历史 source 级 Collector Registry | 旧配置、声明缓存与 source 级读模型 | `collection/hub/Heartbeat.Collection.Hub/Collectors/ICollectorRegistry.cs`、`collection/CONTEXT.md` | Package/Instance/Runtime State 与声明 seam 覆盖所有实际消费者，UI/准入不再读取 Registry 身份 | 依赖搜索为零或只剩明确声明 seam；browser/system/状态 UI 回归 |
| 严格上行协议切换 | 旧 Agent 收到 426，新 Agent 迁移缓存后重传 | `RequireHeartbeatProtocolAttribute.cs`、`SegmentIngestContract.cs`、`UploadStream.cs` | 完成真实旧客户端升级演练，并明确协议版本支持/弃用窗口 | server-first 演练、426 UI、缓存容量、迁移后幂等重传与坏记录隔离 |

## 维护规则

- 新增兼容分支时在同一改动中补一行，或者链接到已有行。
- 移除兼容代码前先保留对应 fixture；移除后把本行改为已退役记录或在 ADR/issue 中留下结果。
- “个人部署只有一个用户”可以缩短支持窗口，但仍需根据真实本地文件和已安装客户端裁决，不能
  仅按代码提交日期猜测。
