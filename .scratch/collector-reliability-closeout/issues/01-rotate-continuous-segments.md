# 01 — 在摄入边界前旋转连续 Segment

Status: done

Owner: Collection / System + VRChat

Priority: P1 — 超过 24h 的 snapshot/final revision 会被 strict ingest 拒绝，导致整段无法形成真实服务端事实。

## What to build

在共享的、可测试的 segment rotation policy 下，让 System active/away 与 VRChat presence 即使观察值
一直不变，也会在小于 `SegmentValidationPolicy.MaxDuration` 的边界 finalize 当前 fact，并从同一
instant 以新 UUIDv7/revision 1 开始下一段。Browser 已有 23h 先例，但三方应消费同一契约 fixture。

## Acceptance

- [x] rotation threshold 明确小于 24h 并留出 clock/upload tolerance；不能等 server 拒绝后补救。
- [x] 旋转产生 `[start, boundary]` final 与 `[boundary, ...]` 新 fact：无 gap、无 overlap、旧 FactId
  不复用，新 FactId 为 UUIDv7、revision 从 1 开始。
- [x] System active、System away、VRChat presence 都在无 observation change 时由时钟触发 rotation；
  普通 app/title/world change 和 stop 语义保持正确。
- [x] crash/restart 只从 durable current snapshot 恢复一次；不会同时继续旧 fact 又创建重复新 fact。
- [x] fake-clock tests 覆盖阈值前、精确边界、跨多边界、边界同时发生状态变化、stop 与 restart。
- [x] 真实 protocol → projection → HTTP 测试证明长于 24h 的模拟会话以多个合法段摄入，合计 union
  duration 与原会话一致。
- [x] `SegmentValidationPolicy.MaxDuration` 仍保持服务端保护边界，docs/fixtures 不复制漂移常量。

## Comments

### 2026-08-30 — TDD 实现暂停，等待 owner 裁决恢复语义

已建立但尚未提交的验证分支包含共享 23h rotation fixture、System active/away、VRChat presence、
Browser fixture 消费，以及真实 System protocol → projection → HTTP 的 25h 长会话测试。当前自动验证：

- `Heartbeat.Core.Tests` 31/31；
- `Heartbeat.Collector.System.Tests` 42/42（包含 rotation 后 500ms stop 的无 gap 回归）；
- `Heartbeat.Collector.VRChat.Tests` 11/11；
- `Heartbeat.Server.Tests` 450/450；`Heartbeat.Collection.Hub.Tests` 205/205；
- Browser `npm test` 77/77 且 production build 成功；
- `dotnet build Heartbeat.slnx --no-restore --configuration Debug` 成功，0 warning / 0 error。

独立 review 发现以下阻塞，故验收项保持未勾选，也未创建 A2 commit：

1. 当前 v1 checkpoint 可以持久化一个已摄入到 rotation threshold 之后的 active fact。若恢复时把旧
   FactId 的 End 缩回 23h 并从 23h 新建 fact，server 的单调 merge 不会缩短旧 End，因此会产生永久
   overlap。需要 owner 决定当前真实 v1 数据如何迁移（不得 shrink，也不能猜测更旧格式）。
2. 现有 VRChat restart 路径先把 restored presence finalize 到 `recoveredAt`，又对
   `[active.End, recoveredAt]` 报 `process_restart` Gap；即 downtime 同时被声明为 presence 与 gap。
   rotation 会把长 downtime 变成多个可摄入 chunk，扩大这一真实语义冲突。需要 owner 决定二者谁是
   权威事实。
3. rotation 的 `[old final, new active]` 目前逐条 publish/ACK 后逐条保存 checkpoint；若 old final ACK
   后、new active publish 前进程崩溃，checkpoint 已清空，continuation 永久丢失且没有 Gap。恢复方案需与
   上述 checkpoint/restart 语义一起裁决。
4. System 仍需实现并测试两个确定性并发约束：在状态锁内取得 transition 时间，避免 boundary tick 与
   stale transition overlap；Stop 必须等待可注入时钟驱动的 snapshot loop 退出，避免 final 后出现更高
   revision non-final snapshot。

恢复实现前需要的最小裁决：v1 active checkpoint 超过新阈值时 continuation 的权威起点，以及 restart
downtime 应表示为 presence、Gap，还是由另一个明确规则切分。裁决后再完成 crash ordering 和 System
timer/stop race，重新执行完整验证并逐项勾选。

### 2026-08-30 — owner 裁决落地并完成

Owner 确认 `active.End` 是最后真实观察点：旧 FactId 只在持久化 End 封口、永不 shrink；restart
downtime 只形成 `process_restart` Gap，新 fact 不从持久化 End 之前开始。实现据此完成：

- 共享 `segment-rotation-policy.json` 固定 23h boundary / 1h tolerance，由 Core C# 与 Browser
  TypeScript 共同消费，并校验 24h server policy 未漂移；Collection glossary 记录该权威术语。
- System active/away 在可注入真实 timer 上 rotation；所有状态时间在同一锁内读取，Stop 等待 in-flight
  tick 退出后才发布 final，rotation 后正数 subsecond continuation 不再被噪声闸门丢弃。
- VRChat checkpoint v2 在 publish 前原子 Stage 整组 facts/gaps/next Active，逐项 ACK 后再持久移除；
  crash 可按稳定 FactId/revision 重放。当前真实 v1 checkpoint 原样保留 Id/Start/End 只增加 final
  revision，downtime Gap 与 final 同批 durable Stage。
- 真实 25h System protocol → projection → HTTP 路径摄入两个连续合法 chunk（23h + 2h），union
  duration 为 25h，dead-letter 为 0。

精确验证：Core 31/31、Collector Protocol 4/4、System 45/45、VRChat 13/13、Hub 205/205、
Server 450/450、Browser 77/77 且 production build 成功；`dotnet build Heartbeat.slnx
--no-restore --configuration Debug` 为 0 warning / 0 error；`git diff --check` 通过。第二轮独立
correctness review 确认先前五项 finding 均关闭且无新 finding。
