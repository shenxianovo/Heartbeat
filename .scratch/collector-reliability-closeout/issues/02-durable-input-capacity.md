# 02 — durable InputEvent 满容量不再静默丢失

Status: ready-for-agent

Owner: Collection / System Input + Protocol

Priority: P1 — 当前 durable cache `Replace` 会 trim oldest，却没有对应 Gap，ACK 连续性被伪造。

## What to build

为 InputEvent durable projection 的容量耗尽建立明确协议结果。首选让 protocol/outbox 的 durable
backpressure 成为唯一容量边界；若必须 evict，则在同一原子 mutation 中持久化覆盖精确序列范围的
Stream Gap。任何路径都不能只调用 `JsonFileCache.TrimToCapacity` 后继续宣称连续。

## Acceptance

- [ ] 指定唯一容量 owner；`InputEventBuffer`、protocol outbox 与 `JsonFileCache` 不再各自静默裁剪同一
  事实窗口。
- [ ] 满容量时 producer 得到可判定 backpressure，或 durable state 同时记录精确 Stream Gap；崩溃点
  不会出现“事件已删但 Gap 未写”或反向不一致。
- [ ] drain/ACK 只删除已确认 event；retry/restart 保持 event/gap 序列和 FactId，不重复吞吐。
- [ ] Current/diagnostic 能区分 backlog、backpressure、gap 与普通 idle，不把 count clamp 当作成功。
- [ ] 故障注入测试覆盖 capacity-1/capacity/capacity+1、并发 enqueue+drain、cache replace crash、restart、
  ACK/retry 与 gap upload。
- [ ] 现有 `DropsOldest` 测试被替换为协议真实性断言；所有 System/Hub/Protocol tests 通过。
- [ ] 对当前真实 cache version 的迁移有 backup/atomic rewrite/recovery 测试；无现场证据的旧格式不猜测。
