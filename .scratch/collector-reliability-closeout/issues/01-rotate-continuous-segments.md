# 01 — 在摄入边界前旋转连续 Segment

Status: ready-for-agent

Owner: Collection / System + VRChat

Priority: P1 — 超过 24h 的 snapshot/final revision 会被 strict ingest 拒绝，导致整段无法形成真实服务端事实。

## What to build

在共享的、可测试的 segment rotation policy 下，让 System active/away 与 VRChat presence 即使观察值
一直不变，也会在小于 `SegmentValidationPolicy.MaxDuration` 的边界 finalize 当前 fact，并从同一
instant 以新 UUIDv7/revision 1 开始下一段。Browser 已有 23h 先例，但三方应消费同一契约 fixture。

## Acceptance

- [ ] rotation threshold 明确小于 24h 并留出 clock/upload tolerance；不能等 server 拒绝后补救。
- [ ] 旋转产生 `[start, boundary]` final 与 `[boundary, ...]` 新 fact：无 gap、无 overlap、旧 FactId
  不复用，新 FactId 为 UUIDv7、revision 从 1 开始。
- [ ] System active、System away、VRChat presence 都在无 observation change 时由时钟触发 rotation；
  普通 app/title/world change 和 stop 语义保持正确。
- [ ] crash/restart 只从 durable current snapshot 恢复一次；不会同时继续旧 fact 又创建重复新 fact。
- [ ] fake-clock tests 覆盖阈值前、精确边界、跨多边界、边界同时发生状态变化、stop 与 restart。
- [ ] 真实 protocol → projection → HTTP 测试证明长于 24h 的模拟会话以多个合法段摄入，合计 union
  duration 与原会话一致。
- [ ] `SegmentValidationPolicy.MaxDuration` 仍保持服务端保护边界，docs/fixtures 不复制漂移常量。
