# 07 — 修复 client disconnect 与 async enumerator Dispose 竞态

Status: ready-for-agent

Owner: Analytics / Recap Streaming

Priority: P2 — 全仓项目并行时 client disconnect cleanup 可对仍在途的 `MoveNextAsync` 并发执行
`DisposeAsync`，把正常断连偶发暴露为 `NotSupportedException`。

## Acceptance

- [ ] `NextChunkAsync` 返回 `ClientGone` 时仍保留在途 `pending` 的 ownership，先取消并等待该
  `MoveNextAsync` 收敛，再执行 enumerator `DisposeAsync`。
- [ ] client disconnect 不产生 error/done event、不落 recap cache，silence/overall timeout 仍保持可区分。
- [ ] 回归不依赖 `Task.Delay(200)` 与调度先后，使用确定性 cancellation/MoveNext/Dispose barrier。
- [ ] 精确回归、Server suite 与完整 solution 项目并行重复通过。

## Comments

### 2026-08-31 — discovered by Collector lifetime closeout gate

在固定 Collector diff 的完整 solution 第 2 轮中，
`Generate_ClientDisconnect_BeatsBothTimeouts_NoErrorEvent_NothingCached` 1/1 失败，堆栈来自
`FakeGenerator.GenerateStreamAsync(...).DisposeAsync()` 的 `NotSupportedException`；相同二进制随后精确运行
10/10 通过，证明不是 Collector 代码回归而是并行调度相关 flaky。

源码中循环在检查 `step.ClientGone` 前无条件执行 `pending = null`，导致 `finally` 无法取消/等待仍在途的
`MoveNextAsync`，随即并发 Dispose。该问题属于 Recap streaming，当前 Collector 任务只记录证据与承接者，
不越界修改 Server production/fixture。
