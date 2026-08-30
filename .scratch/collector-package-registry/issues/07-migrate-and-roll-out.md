# 07 — 迁移真实安装并上线 Registry

Status: ready-for-human

Owner: Release / Operator

Priority: P1 — production signing key、域名路由与真实设备升级需要 owner 操作和观察。

## What to do

在 01–06 与 reliability gate 全部通过后，配置 production Ed25519 key 和独立静态 Registry 部署，发布
首个正式 Browser/VRChat 版本，并只迁移现场能够验证的真实安装状态。

## Preconditions

- issues 01–06 code complete，production dry-run 与 recovery test 有证据。
- [Collector reliability closeout](../../collector-reliability-closeout/PRD.md) 为 done。
- 当前生产 Desktop/Headless、Browser/VRChat PackageId/version/hash/配置/Secret/Stream/LKG inventory
  已以只读方式记录；无证据的旧版本不进入支持承诺。

## Acceptance

- [ ] 在受保护 CI 环境创建/导入专用 Ed25519 私钥；Runtime pin 的公钥/key id 与 production 签名一致，
  AuthService JWT key 未复用。
- [ ] 独立 Registry deployment 可经 `/collector-registry/v1/` 读取，缓存与反向代理不改 bytes；frontend
  deploy/rollback 不改变 Registry 内容，Registry deploy 也不触碰 `/u/` 页面。
- [ ] Browser、VRChat 首次 canonical release 由显式 tag 产生；同版本重发被拒，channel 最后切换。
- [ ] 若现有 exact content hash 匹配首个 release，则登记为该 exact Installation；不匹配则发新 Version
  并走普通 offer，不伪造“已经升级”。
- [ ] PackageId、InstanceId、配置、Secret、Fact Stream 与 LKG 在支持的真实迁移中保持；任何 replacement
  都有显式映射和 owner action，不按名字猜继承。
- [ ] 真实 Desktop Browser smoke：发现 → 下载/验证 → owner 批准 → reload → exact hash Ready；旧 Host
  在门禁前保持可用。
- [ ] 真实 ManagedProcess smoke：批准 → Ready → stability → LKG；另做一次候选 crash 回滚。
- [ ] Registry 离线、withdrawn、坏 channel 与 rollback runbook 实演通过；监控能区分 Registry、download、
  approval、activation 与 stability 失败。
- [ ] 完成证据追加到 Comments；所有门禁完成前本 issue 保持 `ready-for-human`，PRD 不标 done。

## Comments

- 2026-08-30：本 issue 预先标为 `ready-for-human` 是因为 production key、域名和真实设备 smoke 必须由
  owner 执行；这不表示其代码依赖已经完成。
