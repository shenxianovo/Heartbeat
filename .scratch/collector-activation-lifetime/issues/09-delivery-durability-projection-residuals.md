# 09 — 死信与 durable projection 残留 P3

Status: ready-for-agent

Owner: Collection / Collector Protocol

Priority: P3 — 不影响当前不变量，但死信事务与 durable projection 里还留着重复形状、悲观覆盖与一处死标志，
会让后续读代码的人分不清哪一处才是权威。

## Acceptance

- [ ] `CollectorProtocolOutbox` 的两个 `DeadLetter` 重载（`PendingCollectorFact` 与 `PendingCollectorGap`，
  `CollectorProtocolOutbox.cs:317` / `:353`）事务形状相同（读列表 → 追加 → 持久化 → 更新 delivery order），
  抽成一处共享实现，Fact 与 Gap 只提供各自的集合与路径。
- [ ] `_deadLettersDirty` 要么真正在死信写入失败时置位，要么删除；当前它只被置 `false`，
  `PendingRemainderIsDurable => !_dirty && !_deadLettersDirty` 的第二个合取项恒真，属于误导性死代码。
- [ ] `CollectorProtocolClient` 里 `PendingRemainderIsDurable` 为假时把 drain reason 整体覆盖成
  `PersistenceFailed`（`CollectorProtocolClient.cs:228` 附近）的做法需要复核：deadline 与 flush 原因在同时
  发生持久化失败时会被掩盖，应确认「哪个原因优先」是刻意选择并加回归，或改为保留更具体的原因。
- [ ] `ManagedProcessProtocolClient.StopOnceAsync`（`CollectorRuntime.ManagedProcess.cs:1356`）在
  `TerminationCause` 非空时硬编码 `RemainderDurable: false`，会丢弃 Collector 已上报的真实 durable 证据；
  需要确认这是保守兜底还是应改为沿用 client 上报值。
- [ ] `ManagedProcessTerminationProjector.FromFailedStopReason` 只按 `CollectorDrainReason` 投影，不查
  `client.TerminationCause`；超时短路时 Execution cause 可与 client 侧权威 cause 分叉。要么收敛成单一入口，
  要么给出「此处为何不查权威 cause」的显式理由与回归。
- [ ] `DeadLetterCount` / `DeadLetterPath` 在 Fact 与 Gap 同时有死信时只暴露 Fact 路径，
  诊断面需要能表达混合状态。
- [ ] Fact 的 message 级 rejection 在 `Retryable=true` 时由原来的直接死信改为重试（`ResolveError` 统一策略后
  的连带变化），需要补一条明确回归钉住这个语义，避免以后被当成回归改回去。

## Comments

### 2026-08-31 — opened from third-round double review

由 07 收口时的 Standards/Spec 双轴复审提出，两轴一致定级 P3，不阻断 07 的 closeout。这些都是「结论可能对但
权威来源不唯一」的形状问题，适合在下一次触及死信与 durable projection 时一并处理，不建议为它们单独开一次
改动。最后一条是本轮统一 Fact/Gap reducer 的连带语义变化：Gap 侧本来就按 `Retryable` 决定重试，Fact 侧原来
无条件死信，统一后 Fact 也开始尊重 `Retryable`，方向合理但缺回归覆盖。

Spec 轴在 closeout 复核时补入了 `StopOnceAsync` 硬编码 `RemainderDurable: false` 一条，并要求把
`FromFailedStopReason` 那条还原成「Execution cause 可与 client 权威 cause 分叉」而不是仅仅「补注释」。
