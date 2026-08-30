# ADR-045: 非内置 Collector Package 通过签名 Web Registry 独立发布

## Status: Accepted（extends [ADR-040](./040-collector-runtime-and-protocol-foundation.md) 的 Artifact Delivery 轴与 [ADR-043](./043-hub-local-interactive-collector-authorization.md) 的本地 owner 授权边界）

## Date: 2026-08-30

## Context

Heartbeat 已把采集能力建模为 Collector Package、Instance 与 Activation，并区分 Artifact Delivery
和 Execution Driver；但当前官方 Package 仍主要随 Desktop / Headless 仓库构建交付。这样会把 Browser、
VRChat 等 Collector 的修复与宿主应用发布绑在一起，也没有一个可验证的 Web 来源让 Runtime 区分
“发现了候选”“已下载”“owner 已批准”和“新 Activation 真正就绪”。

目标不是远程控制用户设备，也不是复活旧 source-level Collector Registry，而是让每个非内置
Collector 可以显式、独立、可回滚地发布。Heartbeat 只承诺迁移真实存在的安装状态，不为没有证据
的历史版本设计无限兼容分支。System Collector 是 Desktop 的内置能力，交付边界不同。

Analytics + Dashboard 仍属于协调发布的应用平面；Collector Package Registry 是另一条发布平面。
两者共用域名不应意味着共用镜像、生命周期或信任密钥。

## Decision

### 1. 每个非内置官方 Collector 独立、显式发布

- System Collector 使用 BuiltIn Delivery，随 Desktop 发布，不从 Web 自更新。
- Browser、VRChat 及后续非内置官方 Collector 通过显式 tag（例如
  `collector-browser/vX.Y.Z`）发布；普通 `main` 变更不会自动成为用户可见版本。
- 一个 `PackageId + Version` 只有一个 canonical release manifest；其中可以列出多个 OS / arch
  artifact。平台矩阵先分别构建，再由一次 assembly/publish 生成并签署 canonical release。
- 正式发布后 `(PackageId, Version)` 与其 canonical content hash 永久绑定；同版本异内容是完整性
  错误，不允许覆盖。

### 2. Registry 是独立部署的静态、公开读取平面

Registry 以独立静态部署单元托管在
`https://heartbeat.shenxianovo.com/collector-registry/v1/`，由反向代理挂到现有域名；它不打进
Dashboard frontend image，也不位于用户路由 `/u/{username}` 下。

v1 只发布官方 Package，不提供第三方上传、动态数据库或管理 API。每个 Package 使用独立签名的
stable channel，避免共享 mutable index 的发布竞争：

```text
/packages/{packageId}/channels/stable.json
/packages/{packageId}/releases/{version}.json
/blobs/{sha256}
```

blob 与 release record 不可变；发布顺序是 blob、release record、最后原子替换该 Package 的
channel 指针。撤回版本只阻止新的发现/批准并向 owner 显示高优先级动作，不是远程 kill switch，
也不强制停止已经运行的 Activation。

### 3. Registry 内容使用独立 Ed25519 发布信任链

Runtime 内置专用 Ed25519 公钥，验证 channel 与 release metadata 的签名；签名 metadata 绑定
artifact 的精确长度与 SHA-256，下载后再由本地 Package loader 验证 manifest、artifact、schema
与 declarations。私钥只存在于受保护的发布 CI 环境。

现有 AuthService 可以复用来证明“哪个已登录 owner 有权批准这个本地 Instance 的候选”，但它的
OIDC/JWT 密钥不得复用为制品签名密钥。Registry 的读取和签名验证不要求登录，Heartbeat 服务端也
不持有 Registry 私钥。

### 4. Desired State、Offer、Installation 与 Activation 保持分离

Collector Desired State 保存 channel + SemVer range；Resolved Set 与 Installation 保存精确 Version
和 content hash。Runtime 可以自动检查、解析、下载和验证候选，但只能在 owner 对该 Instance 的
不透明 `offerId` 明确批准后改变安装/激活尝试。

外部管理 interface 只暴露 `Current`、`CheckNowAsync` 与 `ApproveAsync(offerId)`，不让 UI 学习
Registry URL、SemVer 求解、文件布局或下载事务。内部深模块以
`PrepareAsync(requirements)` / `OpenInstalledAsync(packageRef)` 拥有 Registry refresh、兼容解析、
下载、验证、content-addressed 安装、原子提交与离线回退；Artifact Delivery 不直接承担 Activation。

批准与 Last-Known-Good 都是 per-Instance：

- ManagedProcess 候选只有到达 Ready 并通过候选稳定窗口才晋升为该 Instance 的
  Last-Known-Good；失败回滚该 Instance。
- ExternalHost Browser 可以 side-by-side staged，但 UI 必须提示 owner reload extension；只有携带
  目标 artifact hash 的新 ExternalHost Activation 达到 Ready 才算更新成功。
- System 不产生 Web offer，也没有独立 Package 审批。

宿主升级的全局兼容 preflight 属于后续 Host Updater module；它可以复用 package resolver，但不把
宿主升级策略塞进 Registry 或 UI update interface。

### 5. 首次迁移保留真实身份，不能猜测继承

首次迁移尽量保留现有 PackageId、Collector Instance、配置、Secret、Fact Stream 与
Last-Known-Good。只有现有精确内容与首个正式 release 的 canonical hash 完全一致时，才可把它登记
为该 release；否则发布新 Version 并执行普通候选流程。新 PackageId 不隐式继承旧 Package 的
Instance、配置、Secret 或 Stream。

在 Registry 实现之前，先关闭已发现的 P1 可靠性缺口：服务端 strict ingest 必须给出真实结果、
长时连续段必须在摄入上限前旋转、durable InputEvent 容量不能无 Gap 静默丢失、InProcess drain
必须有界。Registry 是独立 feature，不借发布改造掩盖数据可靠性问题。

## Consequences

- ✅ 每个非内置 Collector 可以独立构建、签名、托管和显式发布，不再要求 Desktop / Analytics
  同步发版。
- ✅ 公开读取仍具备端到端来源与内容完整性；认证授权和制品签名各守一条密钥边界。
- ✅ UI、Headless 管理入口与 Execution Driver 不需要理解 Registry transport 或安装文件布局。
- ✅ Desired、Resolved、Installed、Approved、Ready 与 Last-Known-Good 不再被一个“版本”字段混写。
- ✅ 相同域名可以复用现有运维入口，同时保持 Registry 的容器、缓存、回滚与发布并发独立。
- ⚠️ Runtime 必须携带公钥并实现签名元数据、hash、兼容求解、原子安装和离线回退，初始复杂度中等
  偏高，但这些复杂度集中在一个 delivery deep module。
- ⚠️ Browser 无法由 Runtime 静默热替换；owner reload 和精确 hash Ready 是不可省略的真实门禁。
- ⚠️ 撤回不是紧急远程停止能力；若未来需要安全 kill switch，必须另立威胁模型与 ADR。
- ⚠️ Host upgrade preflight、第三方 Package、beta channel 与自动批准不属于 v1。

## References

- [`collection/CONTEXT.md`](../../collection/CONTEXT.md) — Package / Instance / Activation、Artifact Delivery 与本文新增术语
- [ADR-040](./040-collector-runtime-and-protocol-foundation.md) — Collector Runtime 与 Protocol 基础
- [ADR-043](./043-hub-local-interactive-collector-authorization.md) — 本地交互式 owner 授权
- [Collector Package Registry PRD](../../.scratch/collector-package-registry/PRD.md) — 实现拆分与验收图
- [Collector 独立交付实现路线图](../architecture/collector-delivery-implementation-roadmap.md) — feature 间与 feature 内 Mermaid 依赖图
- [Repository understanding issue 05](../../.scratch/repository-understanding/issues/05-strict-segment-ingest-outcomes.md) — strict ingest P1
