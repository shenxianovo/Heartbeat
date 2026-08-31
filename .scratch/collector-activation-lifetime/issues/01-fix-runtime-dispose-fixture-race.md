# 01 — 固定已知 Runtime Dispose 测试夹具竞态

Status: done

Owner: Collection / Hub Tests

Priority: P1 — 全仓并行时错误的调度假设会产生假失败，阻塞架构收敛的可信验证。

## Acceptance

- [x] 测试不再假定 activation cleanup 与 Runtime Dispose 中一定由 Dispose 抢到第一次 Stop failure。
- [x] 若测试意图是 Dispose retry，则夹具明确协调 Dispose 已取得 termination ownership 后才释放 activation。
- [x] 只改测试夹具，不修改生产 Stop/重试语义。
- [x] 精确测试重复运行与 Hub suite 通过。

## Comments

### 2026-08-31 — diagnosis

隔离执行通过 1/1；用户提供的证据为隔离 50/50、Hub suite 5 × 222/222，但 solution 项目并行曾复现。
代码证据显示 `StartingCollector.StopAsync` 与 `InProcessCollectorActivation.StopAsync` 都会在失败后把
`_stopTask` 置空；activation failure cleanup 和 Runtime Dispose 都能成为下一次调用者。现有 fixture 的
`blockStreamsOpened` 会响应 Dispose 触发的 cancellation，因此 cleanup 可以在 Dispose 真正等待 Stop 前
先消费唯一的 `stopFailures: 1`。修复应只固定夹具 ownership 顺序。

### 2026-08-31 — deterministic invariant

把 callback 改为同步阻塞后，重新编译的旧断言稳定变红：Dispose 明确观察第一次 Stop failure，但
Collector Stop 只调用一次，旧测试仍要求隐式重试为两次。这证明测试把“失败时保留 Runtime state
ownership”与“某个 caller 再次执行 Collector Stop”混成了一个调度契约。测试现改名并只断言稳定
生命周期结果：第一次 Dispose 失败时 competing Runtime 仍不能打开 state；activation 终止后再次 Dispose
完成并释放 state。底层 Stop retry policy 留给新的 lifetime Module，不通过当前 fixture 指定。

验证：重新编译后旧 `StopCalls == 2` 断言精确 red 0/1；改为 lifecycle invariant 后精确回归连续
50/50，Hub suite 222/222。生产文件零改动。
