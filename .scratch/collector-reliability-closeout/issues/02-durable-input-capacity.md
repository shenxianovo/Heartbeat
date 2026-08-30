# 02 — durable InputEvent 满容量不再静默丢失

Status: done

Owner: Collection / System Input + Protocol

Priority: P1 — 当前 durable cache `Replace` 会 trim oldest，却没有对应 Gap，ACK 连续性被伪造。

## What to build

为 InputEvent durable projection 的容量耗尽建立明确协议结果。首选让 protocol/outbox 的 durable
backpressure 成为唯一容量边界；若必须 evict，则在同一原子 mutation 中持久化覆盖精确序列范围的
Stream Gap。任何路径都不能只调用 `JsonFileCache.TrimToCapacity` 后继续宣称连续。

## Acceptance

- [x] 指定唯一容量 owner；`InputEventBuffer`、protocol outbox 与 `JsonFileCache` 不再各自静默裁剪同一
  事实窗口。
- [x] 满容量时 producer 得到可判定 backpressure，或 durable state 同时记录精确 Stream Gap；崩溃点
  不会出现“事件已删但 Gap 未写”或反向不一致。
- [x] drain/ACK 只删除已确认 event；retry/restart 保持 event/gap 序列和 FactId，不重复吞吐。
- [x] Current/diagnostic 能区分 backlog、backpressure、gap 与普通 idle，不把 count clamp 当作成功。
- [x] 故障注入测试覆盖 capacity-1/capacity/capacity+1、并发 enqueue+drain、cache replace crash、restart、
  ACK/retry 与 gap upload。
- [x] 现有 `DropsOldest` 测试被替换为协议真实性断言；所有 System/Hub/Protocol tests 通过。
- [x] 对当前真实 cache version 的迁移有 backup/atomic rewrite/recovery 测试；无现场证据的旧格式不猜测。

## Comments

### 2026-08-30 — TDD 实现与 friction closeout 完成

`InputEventBuffer` 现在是 Hub committed Event → Analytics 上传之间 durable projection 的唯一容量
owner。投影满容量会抛出可判定 backpressure，Runtime 返回 Retry，Collector Protocol outbox 保留同一
FactId；只有 confirmed drain 会删除已 ACK event。所有 InputEvent `JsonFileCache` 实例改为只做原子
持久化、以 `int.MaxValue` 保留当前 v2 内容，不再在投影层之外 trim。原先 `DropsOldest` 断言已替换为
capacity-1/capacity/capacity+1、over-capacity restart、ACK/retry 与并发 drain/enqueue 的真实性测试。

平台 hook 不能阻塞，因此 System 原始 ingress 的 emergency overflow 在返回前写入独立 durable Gap
ledger；claim/in-flight/ACK 都是原子 mutation，崩溃重放保持稳定 GapId。Collector outbox 自身容量淘汰
也在同一 JSON mutation 中写 Gap，Event 与零时长 Segment 均转换为可被 Hub 接受的最小 1 tick 半开
区间。诊断状态新增 `Backlog`、`Backpressure`、`GapRecorded`，Gap ACK 后无 backlog 会明确回到
`Ready`。

独立 review 发现 Hub 过去只按 stream/range/reason 去重，会吞掉同一 instant 的独立 loss。friction
closeout 将稳定 UUIDv7 `GapId` 贯穿 InProcess、stdio/ManagedProcess、Browser HTTP 与 Hub durable
state；相同 GapId/不同内容明确 conflict，不同 GapId 即使范围相同也分别提交。当前 Browser durable
记录会在 `loadDurable` 返回前原子补 GapId；当前 Hub runtime state v2 中无 GapId 的 committed Gap
只在精确匹配 lost-ACK retry 时原子绑定该 ID 并返回 Duplicate。两处兼容只覆盖当前真实格式，代码已
记录移除门槛。当前 Collector outbox schema v1 曾生成的零长度 Event eviction Gap 也会原子改写为
1 tick 半开区间而不 quarantine 仍保留的 Facts；未猜测更旧 schema。

验证：System 53/53、Hub 208/208、Collector Protocol 8/8、Mac 78/78、Windows 47/47、VRChat
13/13、Reference ManagedProcess 1/1、Headless 6/6、Desktop UI 25/25、Browser 77/77 且 production
build 成功；`dotnet build Heartbeat.slnx --no-restore --configuration Debug` 为 0 warning / 0 error；
`git diff --check` 通过。现有 `JsonFileCache`/`HeartbeatCacheFormats` 测试继续证明 current InputEvent
v1→v2 backup、原子 rewrite、失败不替换与 restart recovery。独立复核确认原两项 P1/P2 finding 已
关闭；随后发现的 current-state lost-ACK 迁移边角也以失败测试关闭。
