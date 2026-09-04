# ADR-051: 通用 ExternalHost 身份所有权与 Browser 独立交付

## Status: Accepted

## Date: 2026-09-04

## Context

ADR-049 已把 Browser 专属 runtime、路由、安装入口、AppHint resolver 和 UI 从 Host composition 删除，
ADR-050 也已建立按 Host target 浏览 Catalog、安装精确 Package 并自动创建默认 Instance 的通用
Marketplace。然而 ExternalHost 的真实接入仍缺失：默认 handler 只返回 404，当前 Runtime 又以整个
Collector Instance 为 writer 冲突范围，因此一个 Instance 不能同时承载多个浏览器/Profile。

旧的 ADR-040 §5 还把 Chrome、Edge 建模成不同 Collector Instance，并由 Collector 上报 `appHint`、Host
adapter 再解析为 `AppIdentityKey`。这要求 Host 间接理解具体 Collector 的产品词汇，也让用户面对没有独立
启用价值的多个 Instance。另一种做法是在 Package 中声明 AppHint 映射和人工加载说明，但这仍增加了 Host
必须解释的 schema；当前单用户自用阶段不需要这层间接性或引导 UI。

## Decision

### 1. ExternalHost 使用一个通用接入绑定

Desktop 提供一个不含 Package 名称的 loopback Collector Protocol 路由；ExternalHost `hello` 携带精确
Package/Artifact 身份、`externalHostIdentity`、`appIdentityKey` 和协议能力。通用 handler 只从已验证的
Installation 与 Runtime-owned Instance 解析连接，验证所选 Artifact 使用 `externalHost` Driver；未安装时
拒绝本次连接，不影响 Desktop 启动。

Host、两个平台 head、共享 UI 与 Analytics 不增加 Browser、Chrome 或 Edge 分支。Package/Artifact 的名字
只存在于对应 Collector、Registry 数据与面向通用 interface 的测试 fixture。

### 2. 一个 Browser Package 对应一个 Machine-scoped Instance

Marketplace 首次安装 Browser Package 时，按 Package 的 Default Instance Blueprint 自动创建一个
Machine-scoped Collector Instance。Chrome、Edge 与不同 Profile 不形成独立 Instance；它们没有独立的启用、
配置或卸载意图。

Instance 继续表达跨重启稳定的用户意图，Activation 表达某个外部宿主的一次连接，Fact Stream 表达具体输出
及其持久交付身份。安装成功但当前没有 ExternalHost Activation 时，管理事实为 `WaitingForExternalHost`，
界面简写为“等待连接”；这不是 Activation 启动失败。

### 3. Activation 与 writer lease 按 External Host Identity 隔离

每个浏览器/Profile 首次运行时生成并持久化独立的 `externalHostIdentity`。Browser Collector 自己识别当前
宿主 App，并直接发送平台稳定的 `appIdentityKey`；Host 不再解析 `appHint`。`appIdentityKey +
externalHostIdentity` 是 Browser 输出的 identifying dimensions，因此不同外部宿主在同一个 Instance 下打开
不同的持久 Fact Stream，可以并行写入。

同一 `externalHostIdentity` 重连时，新 Activation 只替换该身份的旧 Activation 并复用原 Stream；其他身份
不受影响。同一身份后来声称不同的 `appIdentityKey` 时拒绝连接并记录结构化身份冲突，不静默改绑。Runtime
的冲突检查和撤销必须以 External Host Identity / Stream writer lease 为范围，不再以整个 Instance 为范围。

### 4. Backend 接收 Collector 提供的稳定 App 身份

通用 Segment 投影直接读取 Stream dimension 中的 `appIdentityKey`。Backend 同时得到 Collector `Source`、
稳定 App identity 与页面 `identityKey`；即使 App Catalog 暂时没有该 identity，也按既有规则保留真实值并创建
provisional App，不阻断采集。`ICollectorAppHintResolver`、`App Hint` 协议维度及其 Null adapter 退役。

### 5. 安装、卸载和发布保持开发期最小范围

Desktop 原生管理界面直接复用共享 Marketplace/Runtime：Catalog 只展示当前平台最新版，点击安装后完成
下载、Installation 与默认 Instance 创建。状态文案保持简短，例如“等待连接”“运行中 · 2 个连接”“连接冲突”；
详细原因只进入展开诊断。

完整卸载会撤销该 Instance 下全部 ExternalHost Activation，再删除 Instance、Secret、per-Instance data 与
Installation；仍留在浏览器里的扩展之后只能收到 `package_not_installed`。Host 不尝试安装或移除浏览器扩展。

本阶段不定义 `manualSetup`/`manualRemoval` schema，不提供打开目录按钮，也不执行 Package 提供的命令。
Browser 操作者自行从 Installation 加载扩展。

Browser Collector 由自己的显式 tag 构建一份确定性 Package；相同字节登记到 Windows/macOS、x64/arm64
四个 Catalog target。首版只承诺并真实验证 Chrome 与 Edge。当前只实现首次安装与完整卸载，不实现已安装
Package 的更新、热切换、LKG 或自动扩展更新。

## Consequences

- ✅ 新增 ExternalHost Collector 不需要修改 Hub、Desktop platform head、共享 UI 或 Analytics 的具名逻辑。
- ✅ 一个 Browser Instance 可以同时承载多个浏览器/Profile；重连只影响同一身份，不再产生 Instance 级
  writer 争用。
- ✅ Collector 直接拥有自己能判断的 App 身份知识，Host 不维护可漂移的 AppHint 映射。
- ✅ Installation、Instance、Activation 与 Stream 继续表达各自真实生命周期；“已安装但未连接”不伪装成
  失败。
- ✅ Browser Package 与 Desktop Release 独立，发布任一方都不要求重建另一方。
- ⚠️ 当前自用版本没有扩展加载引导；安装 Package 后仍需操作者手工 Load unpacked。
- ⚠️ ExternalHost 只能撤销本地 lease，不能强制停止或卸载外部应用里的扩展。
- ⚠️ 已安装 Browser Package 的版本更新明确留到后续重新设计，本阶段需要先卸载再安装。

## Amends

- **[ADR-040](./040-collector-runtime-and-protocol-foundation.md) §5**：Browser 不再按 App 建多个 Instance；
  ExternalHost identifying dimensions 从 `appHint + externalHostIdentity` 改为 Collector 直接提供的
  `appIdentityKey + externalHostIdentity`。
- **[ADR-049](./049-named-optional-collectors-outside-host-composition.md) §1/§3**：
  `ICollectorAppHintResolver` 与通用 `appHint` 解析 seam 退役；通用 ExternalHost handler 由默认 404 进入
  真实、仍不含具名 Collector 的实现。

## References

- [ADR-048](./048-shared-collector-host-runtime-and-independent-release-units.md) — 共享 Host Runtime 与独立发布单元
- [ADR-049](./049-named-optional-collectors-outside-host-composition.md) — 具名可选 Collector 不进入 Host composition
- [ADR-050](./050-generic-collector-marketplace-and-runtime-owned-instances.md) — 通用 Marketplace 与默认 Instance
- [Collector Host 与独立交付路线图](../architecture/collector-delivery-implementation-roadmap.md)
- [`collection/CONTEXT.md`](../../collection/CONTEXT.md) — Collection 领域词汇
- [`CollectorRuntime.ExternalHost.cs`](../../collection/hub/Heartbeat.Collection.Hub/Collectors/Runtime/CollectorRuntime.ExternalHost.cs) — 当前 ExternalHost Activation 实现
- [`SegmentFactProjector.cs`](../../collection/hub/Heartbeat.Collection.Hub/Collectors/Runtime/SegmentFactProjector.cs) — 当前 AppHint 投影路径
