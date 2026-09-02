# Collector Host Runtime 与独立 Package 交付

Status: needs-triage

## Problem

Collector Runtime、Protocol 与三类 Execution Driver 已经存在。第一条 tracer 已把 VRChat 移出 Headless
image，并建立 Desktop/Headless 共享的 Installation module；Backend workflow 也已停止顺带部署 Headless。
当前仍缺 Headless 独立 deploy、服务器 Package provision、Collector tag/Web 发布以及 Browser 独立交付，
因此各发布单元尚未全部形成可部署闭环。

2026-09-01 以前的 Registry/Approve/Switch 实现已撤回。旧 issues 01–07 均是历史规格，除非按
[ADR-048](../../docs/adr/048-shared-collector-host-runtime-and-independent-release-units.md) 重写，否则不能作为
Agent 实现指令。

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
| 02 explicit release pipeline | needs-triage | VRChat tag/static publish 阶段重写 |
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
