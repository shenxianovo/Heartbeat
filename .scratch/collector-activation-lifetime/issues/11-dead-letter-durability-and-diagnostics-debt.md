# 11 — Dead-letter durability 与诊断债务

Status: ready-for-agent

Owner: Collection / Collector Protocol

Priority: P3 — 开发期接受死信与 outbox 不具备断电级单事务保证，但必须把边界写清，避免被误报为已解决。

## Acceptance

- [ ] `CollectorProtocolOutbox` 的 Fact/Gap `DeadLetter` 重复事务形状收敛成一处共享实现。
- [ ] 明确记录 dead-letter 与 outbox 两个文件移动之间崩溃可能产生双重状态；需要生产级保证时再引入
  单一 journal/recovery protocol，且文件 I/O 不应长期占用 delivery ownership 临界区。
- [ ] `_deadLettersDirty` 要么真实参与失败状态，要么删除。
- [ ] `PersistenceFailed` 与 deadline/flush 同时发生时的 reason 优先级有明确决定和回归。
- [ ] Fact 与 Gap 混合死信的诊断能表达两个路径；Fact `Retryable=true` 行为有显式回归。

## Comments

### 2026-08-31 — split from issue 09

owner 为开发期 Web Delivery 选择精简可靠性 gate：termination truth 仍是 P2 blocker，本 issue 的断电级
dead-letter 原子性与诊断整理不阻塞第一条 VRChat 纵切。
