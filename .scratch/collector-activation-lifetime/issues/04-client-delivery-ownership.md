# 04 — Client 侧显式 Delivery Ownership

Status: done

Owner: Collection / Collector Protocol Client

Priority: P1 — handoff、cancellation 与 persistence failure 当前共享异常控制流。

## Acceptance

- [x] background → drain → fenced 由显式 delivery ownership/lease 表达。
- [x] drain transition 一次线性化完成 admission close、ownership transfer、epoch advance、deadline capture。
- [x] Superseded 是领域 outcome，不由 `OperationCanceledException` catch filter 表达。
- [x] caller/deadline cancellation、persistence、Stop 与 completion failure 保持可区分。
- [x] Fact/Gap、cooperative/deadline/failure 使用同一状态模型。
- [x] observer/barrier 测试迁移到 Module Interface，test-only production observer 删除。

## Comments

### 2026-08-31 — vertical slice complete

`CollectorDeliveryOwnership` 以 generation-bearing background/drain delivery lease 和 ordinary/tail admission
lease 取代 `CollectorDeliveryCommitFence`。`BeginDrain` 在同一 gate 内关闭 ordinary admission、推进 epoch、
supersede background lease、转移 drain delivery 并保留 Hub 提供的绝对 deadline；文件准备与协议 I/O 均在
锁外。Fact/Gap ACK 统一返回 `Committed / Superseded / Fenced`，step reducer 另保留
`PersistenceFailed`，不再用 `OperationCanceledException` 表示 handoff。

`ICollectorProtocolApplication.StopAsync` 已改收 `CollectorDrainContext`，Stop 只能提交 drain-tail；seal 后
由唯一 drain lease 完成 final flush。旧 observer/barrier fixture 与 production hook、旧 commit fence 均删除。
Interface 测试覆盖 admission/drain 原子转换、Fact/Gap supersede 后收敛、deadline fence 与 prepared ordinary
admission 被 supersede。Protocol suite 30/30；关键 ownership/persistence cases 连续 20 轮、每轮 7/7。
