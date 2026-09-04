# 09 — 具名可选 Collector 移出 Host composition

Status: ready-for-human

Owner: Collection / Host Runtime

Priority: P1 — issue 08 只做到「缺席时降级」；只要宿主代码里还写着某个 Collector 的名字，发布单元独立就
还是半成品：加一个或去一个可选 Collector 仍要改 Hub、两个平台 head、共享 UI 与宿主测试。

Supersedes: [issue 08](./08-host-startup-independent-of-optional-collectors.md) 的宿主解耦部分与其未执行的
Desktop Release 门禁。决策见
[ADR-049](../../../docs/adr/049-named-optional-collectors-outside-host-composition.md)。

## What to build

把宿主组合收敛到通用领域概念：

1. Desktop 与通用 Hub Runtime 不再认识 Browser 这个具体 Collector——删除宿主侧的 Browser runtime、
   protocol handler、安装目录默认值、平台 AppHint 知识、状态模型与 UI 条目。
2. 宿主只组合通用 seam：Collector Package Installation、Collector Runtime、Instance、Activation、Execution
   Driver、Collector Protocol，加上唯一允许写死的 System BuiltIn。
3. loopback ExternalHost 承载改成通用形态：默认 handler 返回 404，接入能力由后续 issue 提供。
4. Desktop startup smoke 变成宿主通用 smoke，并且不碰真实用户数据。
5. Headless 管理面只报告真实事实：逐 Instance 隔离、真实失败原因、缺失即 `null`、显式 readiness signal。
6. Segment 投影由通用 `FactKind` / capability 驱动，Analytics 不替非 BuiltIn Collector 写默认声明。

本阶段接受的代价：Browser 在 Desktop 里没有宿主接入能力，也从 UI 消失。

## 不在范围内

- 通用 ExternalHost 的安装入口、discovery 与握手 adapter（后续 issue，见「已知残留」）；
- Collector tag workflow、可下载 Package 与 Web Package source（issue 02/07 待重写）；
- Browser 扩展自身的代码、Package 构建脚本与 npm 测试改动；
- 恢复 issue 04/05 已 wontfix 的 approval / LKG 切换路径。

## Acceptance

- [x] `AddHeartbeatHub` 只组合通用运行时；Hub 不再导出任何 Collector 专属 binding 扩展方法
      （`AddBrowserExternalHostBinding` 删除）。
- [x] Hub 删除 `BrowserCollectorRuntime`、`BrowserExternalHostProtocolHandler`、`ExternalHostLeaseMonitor`
      与 `CollectorSegmentUploadRequest`；保留的通用 seam 是 `CollectorPackageInstallations`、
      `CollectorRuntime` 及 Instance / Activation / Driver / Protocol、`IExternalHostProtocolHttpHandler` +
      `NullExternalHostProtocolHttpHandler`、`ExternalHostProtocolWorker`、`ICollectorAppHintResolver` +
      `NullCollectorAppHintResolver`。
- [x] loopback HTTP server 的措辞与行为是通用 ExternalHost：默认 handler 一律 404，
      `/v1/collector-protocol/browser` 路由不再存在。
- [x] `facts.segment/v1` 统一走 ActivitySegment projector，不按 schema id / major 列举 Browser、VRChat 或
      Reference；Package schema 先验验证与通用 `identityKey` 基础形状拒绝仍可判定。
- [x] `ExternalHostCollectorActivation`、`CollectorPackageInstallations` 与 Hub `README.md` 的措辞不再提具体
      浏览器。
- [x] Desktop 删除 `WindowsBrowserSetupLauncher`、`MacBrowserSetupLauncher` 与两个平台的
      `*CollectorAppHintResolver`；Chrome/Edge 的进程名与 bundle id 知识不再留在宿主。
- [x] 两个平台 `*AgentHostExtensions` 删除 `browserPackageSourceDirectory` 参数、`CollectorPackages/Browser`
      默认路径、Browser binding 与平台 AppHint resolver 注册；只剩 `AddSystemCollectorInProcessBinding`，
      并把本机数据树统一挂到可注入的数据目录（Windows 走 `ConfigManager.DataDirectory`，Mac 走
      `MacAgentPaths.DataDirectory`）。
- [x] `WindowsDesktopState` / `MacDesktopState` 删除 Browser runtime 依赖、Browser 分支、
      `SetBrowserCollectorAppEnabled`、`OpenBrowserCollectorSetup`、`MapBrowserRuntime` 与相关事件订阅。
- [x] 共享 presentation 删除 `ExternalHostRuntimeStatus`、`BrowserKind`、`BrowserCollectorState`、
      `BrowserCollectorAppState`、快照上的 `BrowserCollector` 属性与 `IDesktopState` 的两个 Browser 方法。
- [x] 通用 ExternalHost / Instance UI 到位前，Desktop presentation 只显示 System，不再从历史 source 级
      Collector Registry 生成伪外部卡片；对应 presentation interface、ViewModel 与 AXAML 分支删除。
- [x] `DesktopStartupSmoke` 不解析任何 Collector 专属状态，只证明 host 起停成功且 System BuiltIn 的
      `CollectorRuntime` 已注册；新增 `--verify-startup-data-directory=<path>`，缺省在临时目录下开一次性目录
      并在跑完删除，两个平台 `Program.cs` 在 smoke 模式下把配置与日志一起重定向到该目录。
- [x] `HeadlessFleetManager`：已有 mapping 的恢复过程逐 Instance 隔离，失败的那条是管理面 `Failed`；
      Activation 失败的 `StatusDetail` 来自真实 `CollectorRuntimeFailure`（code / message / exit code）；
      Instance 没建起来时 `PackageVersion` / `PackageContentHash` 是 `null`；新增 `Initialized` readiness
      signal，零 Instance 测试不再固定睡 100ms。
- [x] 从 issue 08 保留：Desktop 构建/产物不含 Browser、System 随 `dotnet publish` 走 publish target、
      Headless 支持零 Instance 启动、单个 Headless Instance 失败只废掉自己、Release 断言 System 在且
      `CollectorPackages` 下没有其他内容、packaged-host startup smoke。
- [x] Hub.Tests 不导入 Browser build target，也不引用 VRChat 产品；VRChat ManagedProcess E2E 由
      VRChat.Tests 拥有。
- [x] Analytics 启动只插 System BuiltIn declaration；非内置 Collector 测试显式提供 declaration，已有 DB
      声明继续经通用路径生效。

## 已知残留

1. **通用 ExternalHost 安装/连接能力缺失**。默认 handler 一律 404，宿主没有安装入口、discovery 与握手
   adapter，因此 Browser 没有任何宿主接入路径：手工侧载也连不上，UI 里看不到它，
   Browser 的扩展代码、Package 构建脚本与 npm 测试保留并继续由 `collector-contracts.yml` 验证。后续实现已
   由 [issue 06](./06-browser-external-host-update.md) 按 ADR-051 重写，不得恢复 Browser 专属 binding。
2. **Windows 最终包的运行时验证在 macOS 开发机上做不了**：Windows 打包产物的 `--verify-startup`、
   Portable zip 目录层级与 vpk 后的路径只能在真实 tag / Windows 环境上跑，本轮标记为未验证。

## Verification

- `dotnet build Heartbeat.slnx -c Release --no-restore`：0 warning / 0 error。
- `dotnet test Heartbeat.slnx -c Release --no-restore --no-build`：12 个测试程序集，1036 passed / 0 failed / 0 skipped。
- `node scripts/collector-contracts.mjs check`：通过。
- Browser Collector 独立 `npm run build && npm test`：构建通过，8 个测试文件、78 tests 全绿。
- `dotnet publish` 实测 `osx-arm64` 与 `win-x64`：两份 `CollectorPackages` 都只有 `System`，且
  `System/collector-manifest.json` 存在。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 与
  `git diff --check`：通过。
- 宿主生产目录大小写不敏感搜索没有 Browser、VRChat、Chrome、Edge 的语义命中；删除了生产无调用者的旧
  source 活跃度推断 `CollectorActivity` 及其自测，避免测试继续保护退役架构。
- Windows 最终包的运行时验证在本机（macOS）无法执行，标记为未验证。

## Remaining human gate

- Desktop Release 的产物断言与 packaged-host smoke 只在真实 tag push 时执行；本轮没有发 tag。
- Windows 上的打包产物 startup smoke 未验证（见已知残留 2）。
- 通用 ExternalHost 接入能力到位前，「真实 Desktop Browser smoke」这条 PRD 验收无法执行。

## Comments

### 2026-09-02 — 为什么 smoke 的数据目录隔离必须走代码 seam

macOS 上 `Environment.SpecialFolder.ApplicationData` 不认 `HOME` 覆盖，用环境变量伪造隔离会让 smoke 写进
真实用户数据目录。因此隔离做成宿主组合能接受的数据目录参数：smoke 传入目录，宿主照它重定向整棵本机
数据树、配置与日志。

### 2026-09-02 — 消融后的完整收口

消融证明 Hub.Tests 的 Browser build target 可直接删除、Desktop 平台不依赖 source Registry 外部卡片、
Analytics 的 Browser 种子只被五条具名语义测试依赖。收口后 `facts.segment/v1` 统一投影为 ActivitySegment，
协议测试使用 Reference Package，VRChat E2E 回到 Collector 自身测试；Desktop presentation 不再读取
Collector Registry，Analytics 只为 System BuiltIn 建启动种子。Headless 与 smoke 的真实状态缺口也在同一
轮修正。issue 仍为 `ready-for-human`，只因为 Windows 最终包与真实 tag release gate 尚未执行。
