# 03 — Hub 侧单一 Activation Lifetime owner

Status: done

Owner: Collection / Hub Runtime

Priority: P1 — 当前多路径直接 Stop 导致 terminal result、deadline 与 ownership release 竞态。

## Acceptance

- [x] 深 Module 吸收 Starting、Ready、Stop Intent、Dispose、failure cleanup、deadline 与 writer release。
- [x] terminal task/result 对同一 Activation 持久；caller cancellation 只取消等待。
- [x] Module 明确拥有 Stop failure/retry policy，caller 不重置 task 或消费失败额度。
- [x] Runtime Dispose、activation cleanup、update/deactivate/supervision 不再直接执行 Stop。
- [x] Interface tests 覆盖 concurrent Dispose/failure cleanup、failure policy、deadline 与 Ready race。
- [x] 已迁移的旧调度测试与 coordinator 删除，不做 layer。

## Comments

### 2026-08-31 — owner migration complete

`CollectorActivationLifetime` 现在是 accepted Hello/ExternalHost reservation 到 fence/release 的唯一 owner。
同一 Activation 的 first Stop Intent 固定 cause 与绝对 deadline；所有 caller 共享 persistent Terminal，等待
cancellation 不进入事务。InProcess 的两次 cooperative attempt、ManagedProcess 的单次 protocol drain 后
terminate、ExternalHost revoke 都封装在 Driver adapter。`StartingCollector`、Activation/ManagedProtocol
resettable stop task、Runtime direct Stop、ExternalHost `CompleteStop*` coordinator 已删除。

Interface tests 覆盖 concurrent intents、waiter cancellation、Ready/Stop 两种线性化次序、固定 deadline、
内部 retry、永久 failure、fence failure retention 与 cancellation-ignoring deadline。Hub suite 在三 Driver
迁移后 236/236。
