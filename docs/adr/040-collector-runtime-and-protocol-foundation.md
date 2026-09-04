# ADR-040: 统一 Collector Runtime 与 Collector Protocol

## Status: Accepted

## Date: 2026-08-22

## Context

ADR-017 建立了 Collector → 本机 Hub → Analytics 的采集拓扑，但当前契约仍围绕 `ActivitySegment` 与 loopback HTTP 生长：system 在进程内运行，browser 由浏览器运行，VRChat 终端原型未来需要在常驻服务器上持续采集。如果继续按宿主逐个扩展，安装身份、配置、生命周期、版本协商和数据上报会形成多套互不兼容的机制，新增 Collector 仍会迫使 Agent 或 Hub 跟着改造和发布。

DeepSeek Harness / Cordis 的生命周期所有权与 desired/actual 收敛思想可借鉴，但其边界主要在单一进程和 JavaScript 依赖图内，不能替代 Heartbeat 跨进程、跨语言、跨宿主的 Collector 协议。因此本期目标不是实现通用插件生态或细粒度组件热运行时，而是在 Collector 数量增长前固定统一的运行身份与协议边界。

## Decision

### 1. Runtime 分离 Package、Instance 与 Activation

- **Collector Package** 是经过完整性校验的内容快照；同一版本可包含供不同 OS、CPU 架构与执行方式选择的制品，但不表示已经配置或正在运行。当前只有本地官方 Package，SemVer 是声明版本，Runtime 以 content hash 标识具体候选，不跨重启强制同一 SemVer 只能出现一个 hash。正式发布不可变性留到 Package Registry / 发布安装 seam 建立后执行。
- **Collector Instance** 是稳定的配置与期望状态身份；Package 更新、Hub 重启或重新激活不改变 InstanceId。
- **Collector Activation** 是 Instance 的一次协议会话和实际运行状态；每次重启或重连创建新的 ActivationId。
- **Source** 只表示谁作出观测，不承担 Package、Instance 或 Activation 身份。

Runtime 分开持有 Desired、Resolved、Installed 与 Runtime State，不用暂时的安装失败或运行失败改写用户意图。首版本地 Hub 持有 Desired State，Analytics 只观察运行事实；表达本身保持可同步，为未来控制面留边界。

### 2. 制品交付与执行方式是两条轴

Artifact Delivery 表示制品由宿主内置、Runtime 管理，还是由浏览器商店等外部系统管理；Execution Driver 表示代码在 Hub 进程内、Runtime 托管进程中，还是浏览器等外部宿主中执行。两条轴不能合成一个“插件类型”。

因此 system、browser 与 vrchat.account 进入同一 Package / Instance / Activation 模型，但 Runtime 只承诺自己实际拥有的动作。vrchat.account 产品化后作为无头 Hub 同机的 ManagedProcess，经本机协议发送事实；这修订 ADR-032 中进程内 BackgroundService 的预设，但保留多个 Hub 对等直连 Analytics 的星形拓扑。一个 Hub 可以托管面向多个 Subject 的 Instance，不按观测主题拆 Hub。

Collector Package 自包含私有依赖；Collector 之间不共享运行时服务或依赖。更新以单个 Instance 为事务边界，候选 Activation 到达 Ready 后才提交，失败只恢复该 Instance；本期允许通过重启完成切换，不承诺无停机热卸载。

### 3. 一个语义协议，多种 Transport Binding

Collector Protocol 定义与传输无关的握手、版本与能力协商、配置快照、Output Template / Fact Stream 开启、生命周期以及 Fact 交付语义。InProcess、ManagedProcess 与 ExternalHost 分别可以使用类型化调用、stdio/pipe 与 loopback HTTP，但必须通过同一组语义契约测试，不能形成三套协议。

Collector Protocol Client 是宿主无关的库边界，不捕获调用方的 `SynchronizationContext`。平台原生回调只把观测放入 Collector 自有 ingress queue，由后台 delivery pump 执行持久化、背压与协议 I/O；任何 UI、窗口观察或输入 hook 线程都不能同步等待 Fact ACK。

回调成功返回只表示观测进入进程内 ingress queue，不是 durable ACK。System InputEvent 的 durable
capacity 判定属于后台 delivery pump：它必须在同一个 journal mutation 中二选一提交 Event 或覆盖该
Event 的 Stream Gap，不能先删除/拒绝 Event、再写另一份 Gap ledger。进程在 pump 持久化前终止仍是
明确的易失 ingress window；不得通过在平台回调中同步 fsync 来把它伪装成 durable responsibility。

基础协议按 Major 协商；Fact、配置和生命周期能力独立版本化；Package SemVer 不参与 wire negotiation。Manifest 静态声明允许产生的 Source、FactKind、schema、SubjectKind 与 identifying dimensions，Activation 只能把声明绑定为具体 Fact Stream。

> 2026-09-02 projection closeout：`facts.segment/v1` 的可执行投影形状统一为
> `ActivitySegment`。Package 自有 schema id / schema major 只细化 payload，由 Package JSON Schema 先验证；
> Hub 再按通用基础形状要求非空 `identityKey`，不按具名 Collector schema 白名单选择 projector。未来若出现
> 非 `ActivitySegment` 的 Segment，必须升级 capability major 或引入新的通用 projection 声明，不能静默复用
> `facts.segment/v1`。

Instance Desired State 使用单调 SpecRevision；Activation 报告已经应用的 Revision，不能动态应用时由 Runtime 重建 Activation。Collector → Hub 的 Fact 交付采用至少一次与幂等收敛：Collector 在 ACK 前负责保留，Hub 取得持久化责任后才 ACK，重复由 Fact 身份处理。批大小、背压、Stream Gap 和本地存储形态属于协议规范与实现细节，不再单独立 ADR。

### 4. 本期明确不扩张到完整插件生态

本期不实现中心 Package Registry、第三方插件市场、TUF/Sigstore、签名根与撤回、Activation 认证、权限沙箱、资源配额、跨 Collector 依赖或细粒度组件热卸载。协议为这些能力保留可演进位置，但它们不阻塞统一协议与官方 Collector Runtime。

实现阶段已为官方 ManagedProcess Collector 增加 Instance 隔离的 Collector Secret 存储与交互授权；这比本 ADR 最初“不实现 Secret 管理”的范围向前推进了一步，但不构成通用第三方权限或信任体系。

### 5. 实现收敛：Browser 多 App/Host 与旧 loopback 退役

> 2026-09-02 revision note：本节的宿主侧实现已由 [ADR-049](./049-named-optional-collectors-outside-host-composition.md) 修订。"browser 只使用 binding 专属 discovery 与 Collector Protocol v1"这一句不再成立于宿主：Hub 内的 Browser 专属 `CollectorRuntime`、protocol handler 与 `/v1/collector-protocol/browser` discovery 路由已删除，只保留通用 ExternalHost handler seam 与默认 404 实现；browser 的 App Instance、AppHint 解析与 sideload 引导也不再由宿主持有。本节其余协议语义（Package/Instance/Activation、`appHint + externalHostIdentity` 维度、Fact Schema 权威位置）不变。
>
> 2026-09-04 revision note：[ADR-051](./051-generic-external-host-identity-and-browser-delivery.md) 进一步
> 修订本节的身份模型：Browser 一次安装只创建一个 Machine-scoped Instance；浏览器/Profile 以 External
> Host Identity 区分并拥有各自 Stream。Collector 直接提供 `appIdentityKey`，`appHint` 与 Host resolver
> 退役。下列旧规则保留为历史背景，不再指导实现。

- browser 的一份 Package Installation 可在同一 Machine Subject 下形成多个按 App Key 稳定寻址的 Collector Instance；Chrome、Edge 等各自持有 Desired/Runtime/LKG 状态。
- 每个扩展 profile/install 持久化独立 External Host Identity。同一 Host 重连只替换自己的 Activation；同一 App 的其他 Host 和其他 App Instance 可并行。
- ExternalHost Stream 由 `appHint + externalHostIdentity` 形成 identifying dimensions。
- browser 只使用 binding 专属 discovery 与 Collector Protocol v1。`POST /v1/segments`、`GET /v1/hub`、source 级 config/declaration 入口及 fallback 已退役。
- Fact Schema 权威文件集中在 `collection/contracts/facts/`，Package schema 与最终 manifest 由 staging 工具生成并受演进基线约束。
- Package 内的 observation declaration 与 Artifact descriptor 是独立 JSON 契约；跨语言协议行为由 `collection/protocol/conformance/` 的行为语料锁定，wire message shape 由协议/Runtime 代码严格校验。
- Package 的原始文件 hash 只负责内容完整性；Fact Schema 跨版本演进比较解析后的 JSON 含义，排版变化不要求升 revision。同版本的新 Package content hash 可作为新候选走 Ready/LKG/回退流程。

## Consequences

- ✅ system、browser、VRChat 以及未来 Collector 共享一套身份、期望/实际状态和协议语义。
- ✅ Collector 可以独立发布与切换，不必为每次采集能力变化重建整个 Agent。
- ✅ Transport 与宿主差异被限制在 Binding / Driver，不泄漏成不同领域模型。
- ✅ 协议背压与宿主回调隔离；桌面 UI、窗口观察和输入 hook 不承担 Fact 交付延迟。
- ✅ 本期范围收敛在官方 Collector 的运行时和协议地基，不被插件市场与安全体系拖住。
- ✅ 旧 `/v1/segments` 迁移适配层已经删除，官方 Collector 只走统一协议。
- ⚠️ ExternalHost 的安装、停止和回滚能力天然弱于 ManagedProcess，统一模型不能把这种差异伪装掉。
- ⚠️ 允许重启切换意味着本期不保证零停机更新。

## References

- [ADR-017](./017-activity-segment-pluggable-collectors.md) — Collector 与本机 ingest Hub 起点
- [ADR-020](./020-system-collector-through-hub.md) — system Collector 统一经 Hub
- [ADR-026](./026-collector-registry-deactivation.md) — 现有启用意图与 Hub 准入
- [ADR-032](./032-device-as-observed-subject.md) — 无头 Hub 与 VRChat 原始设计，本 ADR 修订其进程边界
- [ADR-037](./037-collection-project-boundaries.md) — Collection 可执行与程序集边界
- [ADR-049](./049-named-optional-collectors-outside-host-composition.md) — 具名可选 Collector 不进入 Host composition，修订本 ADR §5 的宿主侧实现
- [系统架构与协议](../architecture/system-overview.md) — 当前 JSON 契约地图、Transport Binding 与校验链
