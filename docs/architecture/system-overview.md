# Heartbeat 系统架构与协议

本文描述当前实现，而不是远期愿景。Collector 的身份与协议术语以 [Collection Context](../../collection/CONTEXT.md) 和 [ADR-040](../adr/040-collector-runtime-and-protocol-foundation.md) 为准。

## 运行时模块与交互

```mermaid
flowchart LR
  subgraph Desktop["Desktop App · Windows / macOS"]
    UI["Avalonia UI"]
    OS["OS adapters\nforeground / input / power"]
    SYS["System Collector\nInProcess driver"]
    HUB["Collection Hub\nCollector Runtime"]
    PROJ["Fact validation + durable Runtime custody"]
    CACHE["Native Fact upload + exact confirmation\nlegacy caches drain only"]
    LOOP["ExternalHost listener seam\ngeneric binding handler"]

    UI -->|"typed calls · desired state"| HUB
    OS -->|"typed observations\nenqueue-only callbacks"| SYS
    SYS -->|"background delivery pump\nCollector Protocol v1 · typed in-process"| HUB
    LOOP -->|"no adapter installed"| HUB
    HUB -->|"validated Facts"| PROJ
    PROJ --> CACHE
  end

  subgraph Headless["Headless Collection Host"]
    WEB["Vue management UI + nginx"]
    API["Management API"]
    FLEET["CollectorMarketplaceHost\nshared Runtime ownership"]
    CHILD["ManagedProcess Collector\nReference / VRChat"]
    PIPE["Runtime Fact custody + subject status"]
    HCACHE["Native Fact upload\nlegacy per-instance caches drain only"]

    WEB -->|"HTTPS REST polling\nOIDC/JWT"| API
    API -->|"typed commands / status"| FLEET
    FLEET <-->|"NDJSON over stdio\nCollector Protocol v1"| CHILD
    CHILD -->|"VRChat HTTPS API"| VRCHAT["VRChat API"]
    FLEET --> PIPE --> HCACHE
  end

  CACHE -->|"HTTPS REST\nApiKey → session JWT"| ANALYTICS["Analytics ASP.NET Core API"]
  HCACHE -->|"HTTPS REST\nApiKey → session JWT"| ANALYTICS
  DASH["Dashboard Vue SPA"] -->|"HTTPS REST / SSE\nOIDC Authorization Code + PKCE"| ANALYTICS
  ANALYTICS -->|"EF Core / PostgreSQL protocol"| DB[(PostgreSQL)]
```

通用 ExternalHost listener 由 `AddExternalHostCollectorBinding` 注册的
`ExternalHostCollectorProtocolHandler` 承载，route 是 `/v1/collector-protocol/external-host`；两个 Desktop head
都已接入，没有接入这个 binding 的宿主（如 Headless）保留 `NullExternalHostProtocolHttpHandler` 一律 404。
Browser 专属 discovery 路由仍然不存在，也不会再有：这条 route 不认识任何具名 Collector。旧的
`POST /v1/segments`、`GET /v1/hub` 和 source 级配置/声明入口已经退役。`SegmentIngestService` 仅保留旧缓存排空与旧嵌入式 projection seam，不是新 Fact 的出网权威。

Desktop 的平台观察回调不执行协议 I/O：system Collector 先把 Segment / Event 放入 ingress queue，再由后台 delivery pump 持久化并发送。Collector Protocol Client 不捕获宿主 `SynchronizationContext`，因此 Hub 背压不会阻塞 Avalonia UI、macOS LaunchServices 回调或 Windows hook/message-loop 线程。

## Analytics Fact 边界

`POST /api/v1/facts` 原子接收自包含 Subject/Stream 定义、Fact 快照与 Gap。Runtime 保留未确认数据，
成功响应仅确认相应版本。Analytics 将最新事实直接保存到 Segments / Events，每条只有一份 Payload；
ActivitySegment / InputEvent 是查询时由 SQL 生成的业务结果，不再单独持久化。
Dashboard 获取结构化 Payload 与来源元数据。旧表原地迁入家族表，旧缓存通过确定性身份关联，
不保留永久整行档案；旧 segment/input 入口只排空升级前缓存。Segment/Event 使用数据库微秒精度，
Collection/Runtime 保管终态，Analytics 不存 IsFinal；Gap 保留原有 tick 精度。
详见 [ADR-055](../adr/055-fact-storage-by-family.md)。

## ExternalHost 身份语义

```mermaid
flowchart TD
  P["ExternalHost Collector Package Installation"]
  C["Collector Instance"]
  CA["External Host A\nstable externalHostIdentity"]
  CB["External Host B\nstable externalHostIdentity"]
  A1["Activation A1"]
  A2["Activation A2"]
  B1["Activation B1"]
  ES["Independent Fact Streams\nappIdentityKey + externalHostIdentity dimensions"]

  P --> C
  C --> CA --> A1
  CA -. "same Host reconnect replaces only A1" .-> A2
  C --> CB --> B1
  A2 --> ES
  B1 --> ES
```

并发所有权的单位是 (Collector Instance, External Host Identity) 与它打开的 Fact Stream，不是整个 Instance：
同一个 Instance 下不同 identity 可以同时 Ready 并各自写自己的 Stream，同一 identity 重连只以 `LeaseReplaced`
替换自己的旧 Activation。Instance 由 Runtime 拥有，一个 Package 在一台机器上只有一个默认 Instance
（`InstanceKey = "default"`），外部宿主不各自造 Instance。

`externalHostIdentity` 由外部宿主自己生成并持久化；清除其数据或重装会产生新 Host，旧 Host 只作为历史身份
保留。identity 到 `appIdentityKey` 的绑定权威是持久 Stream 的 identifying dimensions，因此宿主重启后同一
identity 仍然不能静默改绑到另一个 App Key。Collector 直接提供稳定 `appIdentityKey`；Backend 暂时不认识该 Key
时仍保留真实身份，Collector 无法可靠识别宿主 App 时不开始 Activation。

连接资格只由本地已验证 Installation 与 `hello` 声明的精确 Package/Artifact 身份决定：未安装得到
`package_not_installed`，version/contentHash/artifactId/artifactHash 任一漂移或声明的不是当前平台被选中的
`externalHost` Artifact 都 fail closed，被拒绝的连接不留下任何持久状态。安装存在但当前没有连接时，管理事实是
`WaitingForExternalHost`，不是 Activation 启动失败。

## Fact 内容与交付

Collector → Runtime → Analytics 传递完整 JSON Payload 和 Fact 身份、Revision、家族时间。
Runtime 持久保存后 ACK；Analytics 原子接受后只确认该批相应修订。
同版本直接比较已保存内容，低版本不能覆盖高版本。消费者按实际字段生成报表/回放投影，
缺失或不适用内容不阻断合法 Fact 保管。没有 Payload 格式注册、Schema 版本或演进基线。
Package 仍验证文件路径、大小和完整性哈希；这些检查不决定 Payload 形状。

## 跨实现 JSON 契约地图

| 文件或命名模式 | 谁读取 | 职责与权威边界 |
| --- | --- | --- |
| `collector-manifest.template.json` | Package staging | 源码中的静态 Package 清单；staging 补齐 Artifact hash/size 后生成 `collector-manifest.json` |
| `observation-depth.declaration.json` | Package loader、Hub declaration uplink | 独立的观测深度/读数声明；不是 Fact Payload，也不从 Payload 推导 |
| `*.artifact.json` | Package loader、Execution Driver | 描述一个可验证 Artifact。Browser 描述完整 sideload 文件集；InProcess fixture 描述其入口内容 |
| `collector-artifact-ref.json` | Browser 扩展、Browser Runtime | sideload payload 指回已验证 Artifact descriptor hash 的最小引用，不是 Package manifest |
| `collector-protocol-conformance.json` | .NET 与 TypeScript 协议测试 | 生命周期、ACK、重试、Gap 与 drain 的跨语言行为向量；不是 wire-message schema，也不是完整 transcript |

Collector Protocol 的消息字段与严格校验由共享协议/Runtime 代码承担，目前没有另一份 wire-message JSON Schema。`collector-runtime.json`、outbox、cache、secret store 与浏览器本地存储等 JSON 是单实现持久化状态，不属于跨实现协议，也不因本表统一命名。
