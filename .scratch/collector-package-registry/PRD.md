# Collector Package Registry：独立构建、签名与 Web 交付

Status: ready-for-agent

## Problem

非内置 Collector 已有 Package / Instance / Activation 与 Execution Driver 语义，但制品仍主要随
Desktop / Headless 构建交付。仓库缺少一个能让每个 Collector 独立显式发布、让 Runtime 验证和
下载、并让 owner 对具体 Instance 明确批准的 Web 交付闭环。

旧 source-level Collector Registry 是配置/声明遗留账本，不能复用为包仓库；现有 AuthService
只适合证明本地 owner 的审批权限，不能承担制品签名。

## Outcome

- 每个非 BuiltIn 官方 Collector 用自己的 tag 独立构建和发布。
- Runtime 从同域名、独立部署的静态 Registry 自动发现、解析、下载和验证候选。
- owner 对每个 Instance 的精确、不透明 offer 明确批准；批准不等于 Ready 或更新成功。
- ManagedProcess 与 ExternalHost 分别按自身真实激活门禁晋升 Last-Known-Good。
- 首次迁移只保留仓库和设备上真实存在、可以验证的身份与内容，不猜测历史兼容状态。

规范决策见 [ADR-045](../../docs/adr/045-independent-web-delivery-for-collector-packages.md)。
跨 feature、feature 内部与各 Execution Driver 的依赖图见
[Collector 独立交付实现路线图](../../docs/architecture/collector-delivery-implementation-roadmap.md)。

## Fixed decisions

### Release and hosting

- v1 只允许官方 Package；不做第三方上传、动态 DB/admin API、beta channel 或自动批准。
- System 使用 BuiltIn Delivery，随 Desktop 发布，不产生 Web offer。
- 非内置 Package 只由 `collector-{name}/vX.Y.Z` 一类显式 tag 发布。
- 同一 PackageId + Version 只有一个 canonical release，包含各 OS / arch artifact；版本和内容 hash
  不可变。
- Registry 是独立静态部署单元，经反向代理服务于
  `https://heartbeat.shenxianovo.com/collector-registry/v1/`；不进入 frontend image 或 `/u/` 路由。
- 每个 Package 有独立 signed stable channel；blob 与 release immutable，channel 最后发布。

### Trust and authorization

- 受保护 CI 中的独立 Ed25519 私钥签署 channel/release metadata；Runtime 内置公钥。
- signed metadata 绑定 artifact length + SHA-256；Package loader 继续验证内部 manifest/artifacts/
  schema/declarations。
- Registry 公开读取。AuthService 只授权“这个 owner 是否能批准本地 Instance 的 offer”，不得复用
  JWT signing key，也不持有 Registry 私钥。
- withdrawn 阻止新发现/批准并显示高优先级告警，不远程停止现有 Activation。

### State and interface

- Desired State = channel + SemVer range + enable/config intent。
- Resolved Set / Installation = exact version + content hash；Installation 使用 content-addressed 路径。
- 外部管理 interface：`Current`、`CheckNowAsync`、`ApproveAsync(opaqueOfferId)`。
- 内部 delivery seam：`PrepareAsync(requirements)`、`OpenInstalledAsync(packageRef)`；负责 refresh、
  solve、download、verify、atomic install 与 offline fallback，不负责 Activation。
- 失败不改写 Desired State；未完成安装不发布为 Installation；候选失败不破坏 per-Instance LKG。
- UI 可以提供“全部批准”，但内部仍逐 Instance 记录批准与结果。

### Driver-specific success

- ManagedProcess：精确候选 Activation Ready 且通过 stability period 后才晋升 LKG。
- ExternalHost Browser：side-by-side stage 后提示 owner reload；新 Host 用精确 artifact hash Ready 后才
  成功，旧 Host 在此前保持可回退。
- Host upgrade compatibility preflight 是后续独立 Host Updater feature；本模块只提供可复用 resolver。

## Entry condition

开始改变用户安装前，先关闭 [Collector reliability closeout](../collector-reliability-closeout/PRD.md)
列出的 P1 数据可靠性缺口。Registry contract、测试 fixture 与离线实现可以提前开发，但 rollout issue
必须等待该 gate。

## Out of scope

- 第三方 Package、用户自签名 trust roots、Registry 写 API。
- 自动批准、强制更新、远程 kill switch。
- Desktop / Headless host 自更新与全局 host-upgrade preflight。
- Analytics + Dashboard 的协调发布原子性；它属于应用发布平面。
- 为没有现场证据的旧版本、旧 outbox 或旧安装格式承诺兼容。

## Delivery graph

1. [01 — 定义并验证 signed static Registry contract](issues/01-signed-static-registry-contract.md)
2. [02 — 建立每个 Collector 的显式 canonical release pipeline](issues/02-explicit-collector-release-pipeline.md)
3. [03 — 实现 package delivery deep module 与原子安装](issues/03-package-delivery-module.md)（依赖 01）
4. [04 — 暴露 per-Instance offer 与 owner approval](issues/04-instance-offer-approval.md)（依赖 03）
5. [05 — 接入 ManagedProcess 的候选稳定与 LKG](issues/05-managed-process-update.md)（依赖 04）
6. [06 — 接入 Browser ExternalHost reload 与精确 hash Ready](issues/06-browser-external-host-update.md)（依赖 04）
7. [07 — 迁移真实安装并上线 Registry](issues/07-migrate-and-roll-out.md)（依赖 01–06 与 reliability gate）

01、02 可并行；05、06 可并行。07 保留真实发布、域名、密钥与设备 smoke 的人工门禁。
