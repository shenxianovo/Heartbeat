# 01 — 在摄入边界前旋转连续 Segment

Status: needs-triage

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

### 2026-08-30 — closeout 复审重开

System rotation 在 boundary 生成新的 FactId/start，但零时长 continuation 会被过滤；若进程在新段
首次正时长 snapshot 前崩溃，continuation identity 与起点均未持久化，也没有 Stream Gap。VRChat
checkpoint v1/v2 兼容读取同时缺少移除门槛、验证证据与责任边界。两项关闭前，本 issue 恢复为
`needs-triage`。

## Reopened acceptance

- [x] System rollover boundary 原子持久化 continuation identity/start，并以 crash/restart 测试证明恢复。
- [x] VRChat checkpoint v1/v2 兼容分支记录服务对象、移除门槛、验证方式与 owner 责任边界。

### 2026-08-30 — System rollover crash recovery 修复

先以 exact-boundary 失败测试证明 rotation 只发布旧 final、没有 continuation；再以 journal reopen
编译失败测试固定 ACK 后 active checkpoint 仍必须存在。System snapshot 现在把旧 final、零长度新
continuation 与 active checkpoint 作为同一 NDJSON mutation 持久化，之后才提交 monitor 内存状态；
并发转场导致计划过期时只条件式清除匹配 checkpoint，不会误清新 Fact。Fact ACK 只 compact 待交付
batch，checkpoint 保留。异常重启以相同 FactId、更高 revision 封成零长度 final，并对
`[last durable End, recoveredAt)` 原子写稳定 `process_restart` Stream Gap，再从 recoveredAt 开始新的
当前观察，因而不把停机时间伪装成 activity。

快照持久化期间不持有 monitor 状态锁，平台 observation callback 不会因后台 fsync 的锁竞争被阻塞；
普通 callback 仍只入内存 batch，由 pump 后台原子 stage。验证：System 56/56；active/away boundary、
journal crash/reopen、真实 timer 与 transition race 五测试重复 10 次 50/50；真实 25h
protocol→projection→HTTP 1/1；`git diff --check` 通过。VRChat v1/v2 兼容退出台账仍待 P2，故 issue
保持 `needs-triage`。

### 2026-08-30 — VRChat checkpoint compatibility debt closeout

`docs/architecture/compatibility-debt.md` 现已记录此分支实际服务的对象：已落盘且只含非 final
`Active` 的 schema v1 `presence.json`；schema v2 是当前写格式，增加 durable pending facts/gaps。
移除不按提交日期猜测，而由 Collection / VRChat owner 盘点所有仍受支持的数据目录，并证明每个
现存 v1 文件在当前 binary 下至少成功 Stage 为 v2、盘点归零且经过明确的 rollback/离线保留窗口。
删除读取分支时必须保留 v1 fixture，并验证 FactId/Start/End 不 shrink、pending restart replay 与
corrupt quarantine；部署清单和窗口尚无现场证据，因此本轮只关闭“缺少退出台账”的 P2，不删除
兼容读取。当前自动证据：`VRChatPresenceCheckpointTests` 5/5。

### 2026-08-30 — rollover durable stage 与转场串行化

最终 Spec 复审复现了新的 TOCTOU：snapshot 在状态锁内规划 rollover、锁外持久化时，平台转场可先
推进内存状态；旧实现随后提交 stale continuation，留下两个 non-final identity。先加入阻塞
`StageDurableBatch` 的失败测试，证明转场与 rollover 交错会产生 ghost continuation；实现将 durable
stage 设为显式状态边界，期间到达的平台观察只入内存 deferred queue 后立即返回，fsync 完成并提交
rollover identity/start 后再按序重放。Stop 先停止 observation source、等待 in-flight stage 与已进入
边界的 deferred observation 提交，再建立 terminal fence，避免静默丢弃已返回的平台回调。

`ISystemSegmentPublisher` 的 stage/recover/checkpoint 方法同时改为强制持久化契约，不再允许测试或新实现
继承默认 volatile/no-op 行为。验证：完整 System suite 61/61；定向
rollover/transition/stop 回归连续 10 轮、30/30。

### 2026-08-30 — deferred observation 保留回调时刻

第二轮 Spec 复审发现 durable stage 期间的 queue 只保留 observation，重放时才读时钟；阻塞期间
先后返回的 B/C 转场会共用释放后的时刻，使 B 整段被零时长闸门吞掉且无 Gap。先加入
确定性 red fixture，阻塞 rollover fsync 时令 B 真实持续 2 秒再转 C；旧实现找不到 B。queue
现在在 monitor state lock 内同时记录 `DesktopObservation + ObservedAt`，持久边界完成后按原时刻
顺序重放；既不让平台 callback 等 fsync，也不丢失真实转场时间。定向多转场、单转场、
Stop 交错 3/3。

### 2026-08-30 — final lifecycle closeout

System rollover continuation、VRChat v1/v2 compatibility ledger、durable stage/transition 串行化与 deferred
observation timestamp 均已完成；后续 System chunk journal 与 unstaged retry prefix 没有改变 final +
continuation 的原子记录语义。最终 System suite 73/73，完整 solution 连续三轮 974/974，真实 Protocol
跨进程 smoke 10/10；第五轮 Spec 与 Standards 代码复审均无 P1/P2。本 issue 验收已完整，状态更新为
`done`。VRChat v1 读取分支仍按 `docs/architecture/compatibility-debt.md` 的盘点归零、rollback/离线窗口
与 fixture 门槛退出，不因本 closeout 被删除。

### 2026-08-31 — 第二轮独立复审重开

VRChat 损坏 checkpoint recovery 在 `lastWrite >= recoveredAt` 时可能产生 `Start == End` 的协议无效
Stream Gap；System rollover 的 deferred observation 也可能被 gate 释放后的新 callback 超车。两项
确定性回归与有序交接修复完成前，本 issue 恢复为 `needs-triage`。
