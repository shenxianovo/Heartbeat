# 06 — 接入 Browser ExternalHost reload 与精确 hash Ready

Status: needs-triage

Owner: Collection / Browser ExternalHost

Priority: P3 — ADR-047 的 VRChat MVP 完成后重新 grill；当前不得作为 active implementation scope。

## What to build

为 Browser approved offer 生成 side-by-side staged 安装说明和 owner reload 状态。ExternalHost 新握手必须
证明其 PackageId/version/content hash，只有与批准 offer 精确一致且 Ready 才完成该 Instance 更新；
此前旧 Host/LKG 保持可用。

## Acceptance

- [ ] stage 不覆盖当前可用 Browser artifact，且返回明确的浏览器/profile 安装位置与 reload 指引；
  Runtime 不声称能替 owner 操作浏览器扩展 UI。
- [ ] approved 后状态为 awaiting external reload；旧 Host heartbeat/事实不能使新 offer succeeded。
- [ ] 新 ExternalHost handshake/Ready 携带并验证 exact PackageId/version/content hash；不一致 Host 被隔离并
  给出结构化诊断，不接管目标 stream。
- [ ] exact Host Ready 后只完成对应 Instance 的更新；并行 profiles/hosts 保留独立 Host identity、stream
  和 lease。
- [ ] reload 超时、浏览器拒绝加载、新 Host crash 或 owner 取消不会删除旧 LKG；可以安全重试或回退。
- [ ] restart 后 staged/awaiting reload 状态可恢复，过期 offer 不被旧 Host 意外完成。
- [ ] Browser extension + NativeMessaging/loopback fixture 覆盖 old-still-online、wrong-hash、exact-ready、
  concurrent-host 与 rollback。

## Dependencies

依赖 issue 04；不得复用旧 source-level Collector Registry 的安装/启用身份。

## Comments

### 2026-08-31 — deferred by lean development MVP

owner 选择先走通 VRChat ManagedProcess；Browser、reload UI 与 ExternalHost Web update 不属于当前完成条件。
