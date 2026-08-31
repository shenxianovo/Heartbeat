# 03 — Hub 侧单一 Activation Lifetime owner

Status: ready-for-agent

Owner: Collection / Hub Runtime

Priority: P1 — 当前多路径直接 Stop 导致 terminal result、deadline 与 ownership release 竞态。

## Acceptance

- [ ] 深 Module 吸收 Starting、Ready、Stop Intent、Dispose、failure cleanup、deadline 与 writer release。
- [ ] terminal task/result 对同一 Activation 持久；caller cancellation 只取消等待。
- [ ] Module 明确拥有 Stop failure/retry policy，caller 不重置 task 或消费失败额度。
- [ ] Runtime Dispose、activation cleanup、update/deactivate/supervision 不再直接执行 Stop。
- [ ] Interface tests 覆盖 concurrent Dispose/failure cleanup、failure policy、deadline 与 Ready race。
- [ ] 已迁移的旧调度测试与 coordinator 删除，不做 layer。

