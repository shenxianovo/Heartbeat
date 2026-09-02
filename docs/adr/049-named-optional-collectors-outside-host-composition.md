# ADR-049: 具名可选 Collector 不进入 Host composition

## Status: Accepted（amends [ADR-040](./040-collector-runtime-and-protocol-foundation.md) §5 的 browser binding 专属 runtime/protocol handler，以及 [ADR-048](./048-shared-collector-host-runtime-and-independent-release-units.md) §2/§3 中宿主 adapter 持有浏览器 loopback binding 的表述）

## Date: 2026-09-02

## Context

ADR-048 把发布单元解开：Desktop 构建与产物不再包含 Browser Package。上一轮实现（`b653b53`）只做到
"Browser 缺席时降级"——宿主仍然认识 Browser。Hub 里有 Browser 专属 `CollectorRuntime` 与 protocol
handler；Desktop 两个平台 head 的组合根接受 `browserPackageSourceDirectory` 并默认指向
`CollectorPackages/Browser`，注册 `AddBrowserExternalHostBinding` 与平台 AppHint resolver（Chrome/Edge
的进程名与 bundle id）；共享 UI 有 Browser 卡片、侧载引导与 Browser 专属状态类型；startup smoke 还要
解析 Browser 状态。

于是发布单元独立，composition 没有独立：一个具名可选 Collector 的加入或退出仍然要改 Hub、两个平台
head、共享 UI、主题样式与宿主测试；"Browser 不存在"这条分支要在这些位置各表达一次。更重要的是，这
掩盖了真正缺失的能力——通用 ExternalHost 的安装与连接入口并不存在，Browser 能连上只是因为宿主替它
写死了一条 discovery 路由和一份 runtime。

## Decision

### 1. Host composition 只组合通用 seam

Desktop 与通用 Hub Runtime 不再认识任何具体 Collector 的名字。宿主组合的允许内容只有通用领域概念：

- **Collector Package Installation**：`CollectorPackageInstallations`；Installation 的权威只在文件系统。
- **Collector Runtime** 及 Collector Instance、Collector Activation、Execution Driver、Collector Protocol。
- **通用 ExternalHost 承载**：`IExternalHostProtocolHttpHandler` 与其默认实现
  `NullExternalHostProtocolHttpHandler`（一律 404），loopback 监听器生命周期由
  `ExternalHostProtocolWorker` 持有。
- **`ICollectorAppHintResolver`**，默认 `NullCollectorAppHintResolver`：App Hint 解析是 adapter 能力，
  不是宿主知识。

`AddHeartbeatHub` 只注册上述通用运行时；Hub 不再导出任何 Collector 专属 binding 扩展方法。

### 2. System BuiltIn 是唯一允许写死的 Collector

宿主里唯一可以按名字出现的 Collector 是 System：BuiltIn Delivery + InProcess Driver，随 `dotnet publish`
产物走，由 `AddSystemCollectorInProcessBinding` 组合。除它以外，Collector 具名出现在宿主代码、宿主 UI、
宿主主题或宿主 smoke 中都视为宿主特化，不接受。

### 3. 宿主侧的 Browser 特化整体删除

Hub 删除 `BrowserCollectorRuntime`、`BrowserExternalHostProtocolHandler`、`ExternalHostLeaseMonitor`、
`CollectorSegmentUploadRequest` 与 `AddBrowserExternalHostBinding`；`SegmentFactProjector` 不再支持
`heartbeat.browser.active-tab-segment`，也不再解析 Browser 专属 attributes，只保留通用 `appHint` 维度解析；
`ExternalHostCollectorActivation`、`CollectorPackageInstallations` 与 Hub `README.md` 的措辞改为通用
ExternalHost。

Desktop 删除 `WindowsBrowserSetupLauncher`、`MacBrowserSetupLauncher` 与两个平台的
`*CollectorAppHintResolver`（Chrome/Edge 进程名与 bundle id 随 Browser 一起离开宿主）；两个平台的 host
扩展删除 `browserPackageSourceDirectory` 参数、`CollectorPackages/Browser` 默认路径、Browser binding 与
平台 AppHint resolver 注册，并把本机数据树统一挂到可注入的数据目录（Windows 走
`ConfigManager.DataDirectory`，Mac 走 `MacAgentPaths.DataDirectory`）。共享 presentation 删除
`ExternalHostRuntimeStatus`、`BrowserKind`、`BrowserCollectorState`、`BrowserCollectorAppState`、快照上的
`BrowserCollector` 属性与 `IDesktopState` 的两个 Browser 方法；`CollectorItemViewModel` 只认两种形态——
System（按采集能力展开）与经 loopback 汇入的外部采集器；`MainWindow.axaml` 的 Browser 卡片与主题里 5 条
`browser-*` 样式删除。

### 4. 启动 smoke 只证明宿主自身

`DesktopStartupSmoke` 不再解析任何 Collector 专属状态，只证明两件事：host 能启动并干净停止，且 System
BuiltIn 的 `CollectorRuntime` 已注册。它新增 `--verify-startup-data-directory=<path>`；不给路径时自己在临时
目录下开一次性目录并在跑完删除，两个平台的 `Program.cs` 在 smoke 模式下把配置与日志一起重定向到该
目录。数据隔离必须走代码 seam：macOS 的 `Environment.SpecialFolder.ApplicationData` 不认 `HOME` 覆盖，
用环境变量伪造隔离会让 smoke 写进真实用户数据目录。

### 5. Headless 的逐 Instance 隔离与真实失败原因

同一原则要求管理面只报告真实事实，不用宿主编造的话术填空。`HeadlessFleetManager` 因此收敛为：已有
mapping 的恢复过程也逐 Instance 隔离，失败的那条变成管理面 `Failed` 而不是抛出去拖垮 Hub；Activation
失败的 `StatusDetail` 来自真实 `CollectorRuntimeFailure`（code / message / exit code）；Instance 没建起来时
`PackageVersion` 与 `PackageContentHash` 是 `null`（"不存在"）而不是空字符串；新增 `Initialized` readiness
signal，零 Instance 场景不再靠固定睡眠等待。

### 6. 本阶段不做的事

不实现通用 ExternalHost 的安装入口、discovery 与握手 adapter，也不实现声明驱动的 segment 投影。两者是
后续 issue，不在本决策内用临时分支顶替。

## Consequences

- ✅ 具名可选 Collector 的增删不再触碰宿主：Hub、两个平台 head、共享 UI、主题与 startup smoke 都不含
  Collector 名字。
- ✅ 宿主只有一处 Collector 写死点（System BuiltIn），"缺席分支"从多处降级逻辑变成"根本不组合"。
- ✅ startup smoke 成为宿主通用断言，并且不再依赖也不再污染真实用户数据目录。
- ✅ Headless 管理面的失败原因与包版本反映真实运行事实，单个 Instance 的失败不再影响 Hub 启动。
- ⚠️ Browser 在本阶段没有宿主接入能力：Desktop 不能安装、发现或连接它，UI 里也看不到它。
- ⚠️ `/v1/collector-protocol/browser` 不再存在；默认 ExternalHost handler 一律返回 404，直到通用接入能力
  落地。
- ⚠️ `heartbeat.browser.active-tab-segment` 不再被宿主投影识别；即使外部宿主设法送达该 schema 的 Fact，
  宿主也不会投影它。
- ⚠️ Browser Collector 自身的扩展代码、Package 构建脚本与独立 npm 测试保留，并继续由
  `collector-contracts.yml` 验证，但没有任何宿主调用者，也不进 Desktop Release。
- ⚠️ `CollectorPackageInstallations` 当前只剩 Headless 一个调用者；Desktop 侧的 Installation 调用者要等通用
  ExternalHost 安装入口才会回来。
- ⚠️ `ActivitySegmentFactProjector.Supports` 仍硬编码列举具名 schema id（含
  `heartbeat.vrchat.presence-segment`），这与本决策同向但未完成：投影应当由 Package 声明驱动，而不是宿主
  写死一张表。

## Amends

- **[ADR-040](./040-collector-runtime-and-protocol-foundation.md) §5**："browser 只使用 binding 专属
  discovery 与 Collector Protocol v1"不再成立于宿主侧。binding 专属 discovery 路由、Browser 专属
  `CollectorRuntime` 与 protocol handler 已删除；Hub 只保留通用 ExternalHost handler seam 与默认 404 实现。
  ExternalHost Stream 由 `appHint + externalHostIdentity` 形成 identifying dimensions 的协议语义不变。
- **[ADR-048](./048-shared-collector-host-runtime-and-independent-release-units.md) §2/§3**：宿主 adapter 清单
  里的"ExternalHost loopback"不再是浏览器专属 binding；§3 表格中 Browser 一行的"默认 Hub Instance =
  Desktop"是目标态而不是当前事实——当前 Desktop 没有任何 Browser 接入路径。"Browser 代码仍由浏览器承载
  并连接 Desktop"这句在通用 ExternalHost 接入能力落地前不成立。ADR-048 的发布单元独立与共享 Host
  Runtime 结论不变。

## References

- [ADR-040](./040-collector-runtime-and-protocol-foundation.md) — 统一 Runtime、Protocol 与 Driver
- [ADR-048](./048-shared-collector-host-runtime-and-independent-release-units.md) — 独立发布单元与共享 Host Runtime
- [Collector Host 与独立交付路线图](../architecture/collector-delivery-implementation-roadmap.md) — 当前状态与剩余顺序
- [`collection/CONTEXT.md`](../../collection/CONTEXT.md) — Collection 领域词汇
- [`collection/hub/Heartbeat.Collection.Hub/Hosting/HubServiceCollectionExtensions.cs`](../../collection/hub/Heartbeat.Collection.Hub/Hosting/HubServiceCollectionExtensions.cs) — 只组合通用运行时的入口
- [`collection/hub/Heartbeat.Collection.Hub/Ingest/ExternalHostProtocolWorker.cs`](../../collection/hub/Heartbeat.Collection.Hub/Ingest/ExternalHostProtocolWorker.cs) — 通用 loopback 监听器与默认 404 adapter
- [`collection/hub/Heartbeat.Collection.Hub/Collectors/Runtime/SegmentFactProjector.cs`](../../collection/hub/Heartbeat.Collection.Hub/Collectors/Runtime/SegmentFactProjector.cs) — 通用 `appHint` 投影与仍待声明驱动的 schema 表
- [`collection/desktop/Heartbeat.Desktop.Windows/Hosting/AgentHostExtensions.cs`](../../collection/desktop/Heartbeat.Desktop.Windows/Hosting/AgentHostExtensions.cs) — Windows 组合根
- [`collection/desktop/Heartbeat.Desktop.Mac/Hosting/MacAgentHostExtensions.cs`](../../collection/desktop/Heartbeat.Desktop.Mac/Hosting/MacAgentHostExtensions.cs) — macOS 组合根
- [`collection/desktop/Heartbeat.Desktop.UI/Diagnostics/DesktopStartupSmoke.cs`](../../collection/desktop/Heartbeat.Desktop.UI/Diagnostics/DesktopStartupSmoke.cs) — 宿主通用 startup smoke
- [`collection/hub/Heartbeat.Collection.Headless/HeadlessFleetManager.cs`](../../collection/hub/Heartbeat.Collection.Headless/HeadlessFleetManager.cs) — 逐 Instance 隔离与真实失败原因
