# 07 — 部署静态 Registry 并完成 VRChat smoke

Status: needs-triage

Owner: Release / Operator

Priority: P1 — 域名路由与真实 VRChat 更新需要 owner 操作和观察。

## What to do

在 01–05 与选定 termination truth gate 通过后，部署开发期静态 Registry，发布首个 VRChat artifact，
走通检查、安装、批准、Ready 与失败保持旧 LKG。Browser、签名和生产迁移不在本 issue。

## Preconditions

- issues 01–05 code complete，dry-run 与失败保持旧 LKG 的测试有证据。
- Activation lifetime issue 09、10 完成。
- 当前真实 VRChat PackageId/version/hash 与 LKG 已以只读方式记录。

## Acceptance

- [ ] 独立 Registry deployment 可经 `/collector-registry/v1/` 读取，缓存与反向代理不改 bytes；frontend
  deploy/rollback 不改变 Registry 内容，Registry deploy 也不触碰 `/u/` 页面。
- [ ] Registry 内容来自服务器独立静态目录，不由 Analytics API 或 frontend image 提供。
- [ ] VRChat artifact 由显式 tag 产生；index 只在 artifact 已上传可读后更新。
- [ ] 真实 ManagedProcess smoke：手动检查 → 下载/验证 → authenticated API 批准 → 启动 → Ready；失败候选
  不破坏旧 LKG。
- [ ] PackageId、InstanceId、配置、Secret、Fact Stream 与 LKG 保持；不匹配内容走新版本，不猜继承。
- [ ] 不把旧 bundled Package 登记为 Web Installation；它只作为旧 LKG，第一个 Web release 是普通候选。
- [ ] Registry 离线、坏 index、错 hash 与候选启动失败均显示可区分结果。
- [ ] 完成证据追加到 Comments；所有门禁完成前本 issue 保持 `ready-for-human`，PRD 不标 done。

## Comments

- 2026-08-30：本 issue 预先标为 `ready-for-human` 是因为域名和真实设备 smoke 必须由 owner 执行；
  这不表示其代码依赖已经完成。
- 2026-08-31：ADR-047 将第一条纵切缩减为 unsigned VRChat development delivery；Browser、生产签名与
  完整迁移移出本 issue。
