# Collector Host Runtime 与独立 Package 交付

Status: needs-triage

## Problem

Collector Runtime、Protocol 与三类 Execution Driver 已经存在。第一条 tracer 已把 VRChat 移出 Headless
image，并建立 Desktop/Headless 共享的 Installation module；Backend workflow 也已停止顺带部署 Headless。
宿主组合已按 [ADR-049](../../docs/adr/049-named-optional-collectors-outside-host-composition.md) 收敛：Desktop
与通用 Hub Runtime 只组合通用 seam 加 System BuiltIn，不认识任何具名可选 Collector。
VRChat 的显式 tag 与不可变 Web Release 已完成真实发布，0.2.0 可从生产 Caddy 的精确 Version 路径读取；
当前仍缺 Headless 独立 deploy、Host Web Package source 与通用 ExternalHost 的安装/连接能力，因此各发布
单元尚未全部形成可部署闭环。

Browser 现在的状态是"有独立发布单元、无宿主接入能力"：它不进 Desktop 构建与产物，扩展代码、Package
构建 target 与 npm 测试留在 `collection/collectors/Heartbeat.Collector.Browser` 并由 `collector-contracts.yml`
验证；但宿主里没有 Browser runtime、protocol handler、安装目录或 UI 条目，`/v1/collector-protocol/browser`
也不存在，因此手工侧载不再能让它连上宿主。通用 ExternalHost 安装/连接是后续 issue。

2026-09-01 以前的 Registry/Approve/Switch 实现已撤回。issue 02 已按 ADR-048 重写；issues 01、06、07
仍是历史规格，重写前不能作为 Agent 实现指令。

## Outcome

- Desktop、Frontend、Analytics Backend、Headless Hub 是独立发布单元。
- System Collector 使用 BuiltIn Delivery，随 Desktop Release。
- Browser、VRChat 与未来非 BuiltIn Collector 各自显式构建和发布 Package。
- Desktop 与 Headless 复用同一个 Collector Host Runtime 与 Collector Package Installation module。
- 当前只支持显式安装/打开一个精确 Package；Web 下载是后续 Package source adapter。

目标拓扑和完整顺序见
[Collector Host Runtime 与独立交付目标架构](../../docs/architecture/collector-delivery-implementation-roadmap.md)。

## Fixed decisions

- Artifact Delivery 与 Execution Driver 正交；Browser 仍是 ExternalHost，VRChat 是 ManagedProcess，System
  是 InProcess。
- 具名可选 Collector 不进入 Host composition（ADR-049）：宿主里唯一可以按名字出现的 Collector 是 System
  BuiltIn，其余 Collector 只通过通用 seam 接入，宿主 UI、主题与 startup smoke 同受此边界约束。
- 统一 Protocol 指语义一致，不要求 InProcess、stdio 与 loopback HTTP 使用相同 transport。
- Shared Runtime 拥有 Installation、Instance、Activation、Driver、Protocol 与 Runtime State。
- Desktop/Headless 保留各自的 Subject 投影、上传、管理入口、UI、平台能力与部署配置 adapter。
- Package 继续经过现有 manifest/artifact/hash 校验；不新增签名 trust root。
- 普通 `main` 只验证；Collector 的用户可见 Package 必须由显式 Collector tag 发布。
- System 不进入 Web Registry，也不形成独立 Update Offer。

## First tracer

外置本地 VRChat Package，同时建立共享 Installation module：

1. 把 Browser 当前 bundled import 的安装行为提取到共享 module。
2. Headless 使用同一个 module 安装宿主挂载的本地 VRChat Package。
3. 从 Headless Dockerfile 删除 VRChat build/package copy。
4. 证明只替换 VRChat Package 并重启 Headless，无需重建 Headless image。

这条 tracer 完成后再分别出票：Headless 独立 deploy、VRChat tag/static publish、Web Package source、
Browser 独立发布与真实 smoke。

## Out of scope

- 自动更新、channel、SemVer solver、后台检查与通知；
- owner approval、offer、候选稳定窗口、LKG 自动切换与热更新；
- Ed25519、密钥轮换、withdrawn、第三方市场；
- 安装 journal、断电恢复、自动回滚、cache GC；
- 为没有现场证据的旧 Package 或 Installation 状态设计迁移。

## Historical issue disposition

| Issue | 状态 | 新路径 |
|---|---|---|
| 01 static registry index | needs-triage | Web source 阶段重写 |
| 02 explicit release pipeline | done | VRChat 0.2.0 已经专属 tag 发布并经公网逐字节复核 |
| 03 shared local installation | ready-for-human | PowerShell CLI 安全/真实构建待跨平台验证 |
| 04 exact package approval | wontfix | ADR-048 明确不做 approval/offer |
| 05 VRChat ready switch | wontfix | ADR-048 明确不做 candidate/LKG switch |
| 06 Browser ExternalHost update | needs-triage | VRChat Web 纵切后按显式 Installation 重写 |
| 07 deploy and smoke | needs-triage | 独立 release units 落地后重写 |

## Exit conditions

- [ ] Headless 与 Desktop 使用同一个 Package Installation module。
- [ ] VRChat Package 可独立于 Headless image 构建和替换。
- [ ] Backend 与 Headless deployment 分离。
- [ ] VRChat 与 Browser 各自通过显式 tag 发布 Web Package。
- [x] System 仍只随 Desktop Release（publish target + Desktop Release 产物断言，issue 08）。
- [x] 宿主启动不依赖可选 Collector：Desktop 构建与产物不含 Browser，Headless 可零 Instance 启动且单
      Instance 失败被隔离（issue 08）。
- [x] 宿主不认识具名可选 Collector：Desktop 与通用 Hub Runtime 只组合通用 seam + System BuiltIn，
      Browser 专属 runtime / protocol handler / 安装目录 / UI 条目全部删除（issue 09）。
- [ ] 通用 ExternalHost 安装/连接能力存在，Browser 由此重新获得宿主接入路径（issue 09 已知残留）。
- [x] `facts.segment/v1` 由 Package `FactKind` 与 schema 驱动通用 ActivitySegment 投影，宿主不再硬编码
      具名 schema id 列表（issue 09）。
- [ ] 三类 Driver 继续通过统一 Protocol conformance。
- [ ] 真实 Desktop Browser 与 Headless VRChat smoke 有证据。

## Comments

- 2026-09-01：owner 再次确认目标是独立发布单元与共享 Runtime，而不是在线候选更新系统；ADR-048
  取代 ADR-045/047 的当前实现范围。

### 2026-09-02 — issue 08 落地：宿主启动不依赖可选 Collector

[issue 08](issues/08-host-startup-independent-of-optional-collectors.md) 已实现（`ready-for-human`，剩下的是
真实 tag 才能执行的 release 门禁）。它把 Desktop 与 Browser 在**构建期**解开：Desktop 不再构建、不再打包
Browser，Browser 缺失/损坏只降级；System 改由 publish target 随产物走；Headless 允许零 Instance 并把单
Instance 失败隔离在管理面快照里。

**Web Delivery 一步都没做**：Browser 与 VRChat 仍没有独立 tag workflow，没有可下载的 Package，也没有 Web
Package source adapter。所以 Browser 现在的唯一安装方式是手工侧载——这不是终态，是「解耦已完成、发布尚未
开始」的中间状态，对应 issue 02/07 待重写。

### 2026-09-02 — issue 09 落地：宿主不再认识具名可选 Collector

上一条对 issue 08 的判断需要更正：那一轮只做到「Browser 缺席时降级」，宿主仍持有 Browser runtime、
protocol handler、安装目录、平台 AppHint 知识与 UI 卡片，所以「解耦已完成」当时并不成立。
[issue 09](issues/09-named-optional-collectors-out-of-host-composition.md) 才把它做完：Desktop 与通用 Hub
Runtime 只组合通用 seam（Collector Package Installation / Runtime / Instance / Activation / Driver /
Protocol）加 System BuiltIn，决策记在
[ADR-049](../../docs/adr/049-named-optional-collectors-outside-host-composition.md)。

代价是 Browser 在本阶段没有宿主接入能力：`/v1/collector-protocol/browser` 不再存在，手工侧载也连不上，
Browser 从 Desktop UI 消失。Browser 的独立 Package 构建与契约验证保留；它恢复连接后可直接使用
`facts.segment/v1` 通用投影，不需要 Hub 增加 Browser schema 分支。剩余缺口只有通用 ExternalHost
安装/连接能力与独立 Web Delivery。

### 2026-09-02 — issue 02 代码完成：VRChat 精确 Web Release

VRChat 现在有独立的 `collector-vrchat/vX.Y.Z` tag workflow：固定构建 `linux-x64` Package，生成确定性 zip
与不可变 `release.json`，再向服务器静态目录追加精确 Version，并从公网逐字节回读。它不创建 current
pointer，也不触碰 Desktop、Headless、Frontend 或 Analytics。issue 保持 `ready-for-human`：生产 Caddy
静态路由、服务器 x86_64 确认和首个真实 tag 尚未执行。
