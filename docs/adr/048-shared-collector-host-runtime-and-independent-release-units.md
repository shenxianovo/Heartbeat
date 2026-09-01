# ADR-048: Desktop 与 Headless 共享 Collector Host Runtime，发布单元彼此独立

## Status: Accepted

## Date: 2026-09-01

## Context

ADR-040 已让 Desktop Agent 与 Headless Hub 复用 `CollectorRuntime`、Collector Protocol 和三类
Execution Driver，但宿主外层仍未收敛：Desktop 构建时同时打入 System 与 Browser Package，Headless
镜像构建时同时打入 VRChat Package，Backend workflow 又同时构建和部署 Headless。结果是运行语义已经
统一，制品与部署生命周期却仍互相绑定。

此前 ADR-045/047 把独立交付扩张到签名、channel、候选批准、Last-Known-Good 切换和安装事务；实现因
多重生效状态权威而撤回。当前开发目标只要求 owner 能显式安装并运行一个精确官方 Package，不为单用户
开发阶段预建完整软件供应链或在线更新控制面。

## Decision

### 1. 发布单元独立

- Desktop、Frontend、Analytics Backend 与 Headless Hub 分别构建、版本化和部署；Backend workflow
  不再构建或重启 Headless Hub。
- System Collector 使用 BuiltIn Delivery，作为 Desktop Release 的一部分构建和发布。
- Browser、VRChat 及未来非 BuiltIn Collector 各自通过显式 Collector tag 构建独立 Package，并由独立
  静态 Web 路径托管；发布 Collector 不要求重建 Desktop、Headless、Frontend 或 Backend。
- 同域名路由不代表同一个发布单元。Collector Package Registry 可以位于
  `/collector-registry/v1/`，但不进入 Frontend image 或 Analytics。

### 2. Desktop 与 Headless 共享一个宿主无关的 Collector Host Runtime module

共享 module 在 `Heartbeat.Collection.Hub` 内拥有以下语义：

- 精确 Collector Installation 的本地目录与打开规则；
- Collector Instance 的稳定身份、配置和所选精确 Package；
- Collector Activation 生命周期与 InProcess、ManagedProcess、ExternalHost Execution Driver；
- Collector Protocol、Fact/Gap 持久责任、Ready、drain 与 Runtime State。

Desktop 与 Headless 只提供宿主 adapter：

- Desktop：Machine Subject、平台观察与输入、原生权限、UI、图标与 ExternalHost loopback；
- Headless：多个 Account/Person Subject、owner-only management HTTP、每 Instance 投影与上传身份；
- 两者：数据目录、Secret 存储、Package 来源、Analytics 上传与状态展示。

共享 module 不拥有平台 UI、原生 hook、OIDC 页面、服务器数据库、反向代理或部署 workflow。Subject
投影和上传先通过宿主 adapter 接入；只有当 Desktop 与 Headless 的重复行为能由同一 interface 表达时，
才继续下沉，不能为了“共用”把两种真实拓扑塞进大量条件分支。

### 3. Artifact Delivery 与 Execution Driver 保持正交

| Collector | Artifact Delivery | Execution Driver | 默认 Hub Instance |
|---|---|---|---|
| System | BuiltIn | InProcess | Desktop |
| Browser | Web | ExternalHost | Desktop |
| VRChat | Web | ManagedProcess | Headless |
| 后续 Collector | BuiltIn 或 Web | 由 Artifact Descriptor 决定 | Desktop 或 Headless |

“统一协议”表示 Activation、Ready、配置、Fact、ACK、Gap 与 drain 语义相同，不表示 Transport Binding
相同。Browser 代码仍由浏览器承载并连接 Desktop；Runtime 不能假装自己能够启动或替换浏览器扩展。

### 4. 当前 Installation interface 保持最小

第一阶段只支持：显式安装一个精确 Package、列出 Installation、按精确引用打开已安装 Package。Package
Installation module 隐藏 manifest/artifact/hash 校验、目录布局和失败清理；Runtime 不执行网络下载，Web
下载只是向该 module 提供 Package 内容的 adapter。

继续复用现有 Package 内容 hash 与 loader 校验，但当前不实现签名、密钥轮换、channel、SemVer solver、
后台检查、自动批准、热切换、候选稳定窗口、自动回滚、安装 journal 或第三方市场。安装失败不得被记录为
Installation，但不承诺断电级事务和自动恢复。

### 5. 第一条 tracer 是外置 VRChat Package

先让 Headless 从宿主挂载的本地 VRChat Package 安装并启动，而不是从 Headless image 内置目录启动；同时
把 Desktop 当前 Browser bundled import 背后的目录复制、验证和 Installation 状态收进同一个共享 module。
这形成两个真实调用者，并立即允许 VRChat Package 与 Headless image 分开发版。随后增加 Web Package
source adapter，不改变 Runtime 或 Execution Driver interface。

## Consequences

- Collector Runtime/Protocol 的既有可靠性工作继续作为共享地基，不因交付实现撤回而重做。
- Desktop 与 Headless 获得一个共享深 module，同时保留各自真实的 Subject、管理和平台差异。
- 第一个可见能力是“只替换外置 VRChat Package 并重启 Headless”，不必先建设在线更新控制面。
- Browser 独立发布仍需要浏览器 sideload/store 的用户动作；Web Installation 不能把 ExternalHost 描述成
  Runtime 托管进程。
- Frontend 与 Backend 是独立构建/部署单元；破坏性应用协议变更仍可能需要协调 cutover。是否要求跨版本
  兼容属于应用平面策略，不进入 Collector Host Runtime。

## Supersedes

- [ADR-045](./045-independent-web-delivery-for-collector-packages.md) 的当前交付目标；签名 Registry 与在线
  候选控制面不再是已接受范围。
- [ADR-047](./047-lean-development-collector-web-delivery.md) 的候选批准与 LKG 切换路径；该实现已撤回。

## References

- [ADR-040](./040-collector-runtime-and-protocol-foundation.md) — 统一 Runtime、Protocol 与 Driver
- [Collector Host 与独立交付路线图](../architecture/collector-delivery-implementation-roadmap.md)
- [`collection/CONTEXT.md`](../../collection/CONTEXT.md) — Collection 领域词汇
