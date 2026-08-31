# 09 — 修正 ManagedProcess termination truth

Status: done

Owner: Collection / Collector Protocol

Priority: P2 — 当前实现仍可能抹掉真实 durable evidence，或让 Execution cause 与 client 已首写 cause
分叉；VRChat Web Delivery 不能建立在这个错误终态上。

## Acceptance

- [x] `ManagedProcessProtocolClient.StopOnceAsync`（`CollectorRuntime.ManagedProcess.cs:1356`）在
  `TerminationCause` 非空时不得硬编码 `RemainderDurable: false`；logical result 必须保留 Collector 已上报的
  真实 durable evidence，并有 termination + durable remainder 回归。
- [x] `ManagedProcessTerminationProjector.FromFailedStopReason` 只按 `CollectorDrainReason` 投影，不查
  `client.TerminationCause`；所有 logical/completion/execution projection 必须从一次原子发布的 termination
  state 派生，不能再用 fallback 覆盖已首写 cause。
- [x] `WasTerminated` 与 `TerminationCause` 不得分步发布；并发 reader 不能观察到 terminated 但 cause 为空。
- [x] `SupersededFailedAdmissionCannotReportMemoryOnlyRemainderAsDurable` 及相关 termination 回归使用 fake time
  和显式 barrier，不让 100ms drain 与 50ms retry 争真实调度。
- [x] Protocol、Hub 定向测试与完整 solution 通过；issue 07/PRD 的 truthful termination 声明重新成立。

## Comments

### 2026-08-31 — opened from third-round double review

由 07 收口时的 Standards/Spec 双轴复审提出，两轴一致定级 P3，不阻断 07 的 closeout。这些都是「结论可能对但
权威来源不唯一」的形状问题，适合在下一次触及死信与 durable projection 时一并处理，不建议为它们单独开一次
改动。最后一条是本轮统一 Fact/Gap reducer 的连带语义变化：Gap 侧本来就按 `Retryable` 决定重试，Fact 侧原来
无条件死信，统一后 Fact 也开始尊重 `Retryable`，方向合理但缺回归覆盖。

Spec 轴在 closeout 复核时补入了 `StopOnceAsync` 硬编码 `RemainderDurable: false` 一条，并要求把
`FromFailedStopReason` 那条还原成「Execution cause 可与 client 权威 cause 分叉」而不是仅仅「补注释」。

### 2026-08-31 — re-triaged for lean Web Delivery

owner 选择先修 termination cause、durable evidence 与真实时钟回归，再开始 VRChat Web Delivery；其余
dead-letter 与诊断残留移到 issue 11，作为开发期非阻断限制。

### 2026-08-31 — termination truth 收口

先红后绿两个 commit：`7bca697` 固定回归，`aaf916b` 落实现，`fada3cb` 把 protocol 侧真实时钟回归改成虚拟时钟。

实现形状：

- `ManagedProcessProtocolClient` 用单个 `ManagedProcessTermination?` 字段原子发布 termination；
  `Terminate` 以 `Interlocked.CompareExchange` 首写取胜，`WasTerminated`、`TerminationCause`、
  `TerminationRequests` 全部从该快照派生，并发 reader 不再可能看到 terminated 但 cause 为空。
- 新增 `LogicalRemainderDurable`，`StopOnceAsync` 的 termination 分支与 `TerminateAfterStopFailureAsync`
  不再硬编码 `RemainderDurable: false`，Collector 已上报的 durable evidence 在 kill 之后仍然成立。
- `ManagedProcessCollectorActivationLifetimeDriver` 的正常、异常与 `ProjectFailedStop` 三条路径都只读一次
  `client.Termination`：有 termination 就投影首写 cause，没有则投影 `ManagedProcessExitedExecution`，
  不再用 `?? DeadlineExceeded/StopFailed` 覆盖权威。`FromFailedStopReason` 仅供 fence 选择要写入的 cause。
- `CollectorActivationLifetime` 的 failed-stop 顺序改为先定 `drainOutcome`、再 `FenceAfterFailedStop`、
  最后投影 execution，projection 因此只可能读到 fence 已写入的 cause。
- InProcess 与 ExternalHost 的 `ProjectFailedStop` 默认语义未变（分别是 fenced 与 lease revoked），
  本次只收敛 ManagedProcess 的 termination 权威，没有新增第二 owner。

回归：`TerminationAfterReportedDrainKeepsCollectorReportedDurableRemainder`（drain 上报 durable 后被
protocol failure kill，仍为 durable remainder）、`FailedStopProjectionUsesTheFirstWrittenTerminationCause`、
`FailedStopProjectionReportsUnterminatedProcessExitAsExited`、
`FailedStopIsProjectedAfterTheDriverFenceWroteTheTerminationCause`。

`SupersededFailedAdmissionCannotReportMemoryOnlyRemainderAsDurable` 改为把 drain budget 花在虚拟时钟上：
Client 与 binding 共用 `VirtualTimeProvider`，barrier 等到 drain deadline timer 被排上后再 `Advance`，
100ms drain 与 50ms 重试不再争真实调度；配套把两处 persistence retry 的 `Task.Delay` 接到既有
`_timeProvider` seam（默认仍是 `TimeProvider.System`，行为不变）。

验证：目标用例 50/50；Hub suite 255/255；完整 solution 项目并行连续三轮 1036/1036。
