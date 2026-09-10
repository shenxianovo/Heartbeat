# Collector Host Runtime：交付现状与剩余验收

2026-09-10 整理。本文只保留当前交付边界和剩余工作；早期 tracer、撤回方案和逐日实施记录见
[原始 PRD](../../.scratch/collector-package-registry/PRD.md) 与关联 issues，不再作为后续操作步骤。
当前模块及协议拓扑统一见[系统总览](system-overview.md)。

## 已落地的边界

- Desktop、Frontend、Analytics、Headless 各自构建/发布；System 随 Desktop BuiltIn 交付。
- Browser、VRChat 等非 BuiltIn Collector 使用独立 tag 发布不可变 Package，普通 main 验证不发布。
- Desktop 与 Headless 复用 `CollectorMarketplaceRuntime`，统一管理 Catalog、Installation、默认 Instance、
  Activation、Driver、恢复、重试和卸载。宿主仅提供平台、Subject、上传与管理 adapter。
- Host composition 只包含通用 seam 和 System BuiltIn；不恢复 bundled Browser、具名 handler、
  Browser/AppHint 映射或挂载 VRChat 包的启动配置。
- Registry Catalog Latest 用于首次发现；运行和离线恢复以 Runtime 持有的精确 Package reference 为准。
  Package 必须通过 metadata、长度、hash、解压路径与 manifest/artifact 校验。
- Instance、配置及实际生命周期归 Runtime；Headless bootstrap 不接受手写 instances、packageDirectory
  或 Subject ID，Desktop UI 不另行复制下载与生命周期编排。

决策依据：[ADR-048](../adr/048-shared-collector-host-runtime-and-independent-release-units.md)、
[ADR-049](../adr/049-named-optional-collectors-outside-host-composition.md)、
[ADR-050](../adr/050-generic-collector-marketplace-and-runtime-owned-instances.md)、
[ADR-051](../adr/051-generic-external-host-identity-and-browser-delivery.md)。

| Collector | 发布 / Delivery | Driver | 宿主 | Runtime 实际拥有的动作 |
| --- | --- | --- | --- | --- |
| System | Desktop tag / BuiltIn | InProcess | Desktop | 构造、启动、停止 |
| Browser | 独立 Collector tag / Web | ExternalHost | Desktop | 安装包、接受/拒绝连接、撤销 lease；不启动浏览器 |
| VRChat | 独立 Collector tag / Web | ManagedProcess | Headless | 安装包、启动/终止子进程 |

统一 Protocol 是语义统一，不是 transport 统一；三类 Driver 继续使用 conformance vectors。
一个 Browser Instance 承载多个浏览器/Profile，各 External Host Identity 拥有自己的 Activation 和 Stream。
这些身份规则不能替代 Desktop Profile 连接隔离。

## 已有证据与未关闭项

此处汇总原 tracker 的状态，不表示本次重新执行了发布或实机验收；完整证据只保留在对应 issue。

| 实施顺序 | 已有结果 | 剩余 gate / 来源 |
| --- | --- | --- |
| 共享安装与 VRChat 外置 | Package 已移出 Headless image，共享安装模块已建立 | [issue 03](../../.scratch/collector-package-registry/issues/03-shared-local-package-installation.md) 的跨平台验证 |
| 独立发布与 Registry | Headless 独立 deploy，VRChat 0.2.1 发布及生产 Marketplace smoke 已完成 | [issue 01](../../.scratch/collector-package-registry/issues/01-static-registry-index.md)、[issue 02](../../.scratch/collector-package-registry/issues/02-explicit-collector-release-pipeline.md) |
| 通用 ExternalHost | 精确 Package 握手、身份级 lease/Stream、卸载和真实 handler 自动测试已实现 | [issue 06](../../.scratch/collector-package-registry/issues/06-browser-external-host-update.md)，保留其人工验收状态 |
| Desktop Marketplace | Windows/macOS 共用管理行为；v4.2.0 实包启动检查已有 CI 证据 | [issue 10](../../.scratch/collector-package-registry/issues/10-desktop-collector-marketplace.md) 的原生 UI/离线 Catalog 验收 |
| Browser 独立发布 | 0.1.0 四 target 发布与公网制品核对已有证据 | [issue 07](../../.scratch/collector-package-registry/issues/07-deploy-and-vrchat-smoke.md) 的真实 Chrome/Edge 采集、上传及卸载验收 |

制品发布与 Host 启动通过，不等于真实浏览器采集通过。目标仍需证明只发布/安装某个 Collector
不重建对应 Host，以及真实 Desktop Browser 与 Headless VRChat 主链路成功。

## 后续工作边界

- 当前正式路径只实现首次精确安装与完整卸载，没有已安装 Browser Package 更新、自动 reload 或 Store 分发。
- [Browser 开发泳道与更新](browser-development-binding.md) 已实现开发 Profile 绑定及启动前本地包更新，
  保留身份/配置/outbox；生产更新规则不变，不关闭上表的正式发布实机 gate。
- 自动更新、channel、SemVer solver、后台通知、owner approval/offer、候选稳定窗口、LKG 自动切换、
  签名密钥轮换、撤回、第三方市场及 cache GC 不因开发更新需求自动恢复。
- 不执行 Package 提供的命令，不引入具名 Collector 的 Host UI/启动逻辑。

新增能力以真实调用者和对应验收证明边界；不重新执行已退役的 bundled import 或手工挂载 tracer。
