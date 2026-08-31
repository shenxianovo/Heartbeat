# 07 — 第三轮生命周期不变量收口

Status: done

Owner: Collection / Collector Runtime + Protocol

Priority: P1 — Terminal、durable ownership、pending delivery、Dispose fan-out 与 ManagedProcess cause 仍存在会
产生永久等待、错误终态、热循环或漏发 Stop Intent 的路径。

## Acceptance

- [x] 每个 `CollectorActivationLifetime.Terminal` 在任意路径只完成一次；普通 operational failure 返回 terminal
  value，不变量/基础设施异常 fault Terminal；覆盖超大正 drain budget、`TimeProvider/CreateTimer` 异常与 caller
  wait cancellation，并收紧公开 options 验证。
- [x] `RemainderDurable` 只由真实 durable ownership 推导；覆盖 ordinary admission 首次持久化 `IOException`、
  `BeginDrain` supersede、final flush 卡到 deadline 的组合，重启证据与 result 一致，persistence/deadline/flush
  原因可区分。
- [x] Fact/Gap 共用穷尽式 pending-delivery reducer；non-retryable Gap rejection 不热循环、不静默丢失，具有明确、
  可持久、可诊断的有限状态/结果；background 与 drain、messageId/GapId 语义、no late commit 均有回归。
- [x] Runtime Dispose 先向快照中的所有 InProcess、ExternalHost、ManagedProcess lifetime 提交
  `RuntimeStopping` intent，再等待所有 Terminal 并聚合错误；单个 release/fence failure 不阻止其他 Activation，
  重试共享 persistent Terminal 且不重跑 stop transaction。
- [x] ManagedProcess termination cause 只有一个权威；`DrainWriteFailed`、`DeadlineExceeded`、`BeforeReady`、
  `StopFailed`、`ProtocolFailure` 的 execution 与 logical drain/completion projection 一致。
- [x] InProcess/ExternalHost Ready prepare/commit、durable publication、writer grant 的重复只在不扩大公共接口、
  不引入跨进程 owner 且明显减少权威来源时收敛；否则记录非阻断 follow-up。
- [x] 保持 wire shape、Fact Schema、outbox 落盘兼容、Package/Instance/Activation 身份、ExternalHost 弱能力及
  Artifact Delivery/Registry 范围不变；不恢复已退役 coordinator/fence。
- [x] 五组确定性回归逐组先红后绿；Protocol/Hub/System、Browser test/build、collector contracts、IDE1006、
  diff check、真实 ManagedProcess/cross-process 通过；最后一次代码修改后完整 solution 项目并行连续三轮
  1033/1033。
- [x] 两个隔离的高推理 subagent 对 `bad584a5fbd8f93e64eabd9068b4b34386e142c5...HEAD` 完成 Standards/Spec
  双轴复审，P1/P2 清零；提交线性、worktree clean、无 push。

## Comments

### 2026-08-31 — opened from fixed-base review

本 issue 承接第三轮架构收口。测试 seam 继续使用 PRD 已冻结的 Hub lifetime、Client delivery ownership、
Execution Driver conformance 与 Collector Protocol conformance；不新增跨进程 owner 或公共 lifecycle surface。

### 2026-08-31 — 五条不变量的根因与架构修法

1. **Terminal 只完成一次。** 原实现把「终止事务的结果」和「调用者的等待」混在同一个 `TaskCompletionSource`
   上，`CreateTimer`/`TimeProvider` 抛出或 caller 取消都可能让 Terminal 永久 pending 或被第二个 completer 覆盖。
   修法是让 `CollectorActivationLifetime` 独占 Terminal 的写入权：operational failure 走 terminal value，
   invariant/infrastructure 异常才 fault，caller 的 `CancellationToken` 只取消等待、不取消事务；
   `ManagedProcessTerminationCause` 的首写用 `Interlocked.CompareExchange` 决胜。公开 options 的时长校验
   收敛到 `CollectorRuntimeTimeout.Validate`：基线里 `_deadline = GetUtcNow() + _drainBudget` 会在锁内抛
   `ArgumentOutOfRangeException`，而 `_winningIntent` 此时已被占位，于是 Terminal 永久 pending；校验前移后
   超大正 budget 在入口就被拒绝。
2. **RemainderDurable 只由真实 durable ownership 推导。** 原实现在 drain 结果里按「有没有 pending」推断
   durable，与 outbox 的实际落盘状态可以漂移。修法是把判定下沉到 `CollectorProtocolOutbox`
   （`PendingRemainderIsDurable`），`PersistenceFailed` / `DeadlineExceeded` / `FlushCancelled` 三种原因在
   outbox 侧各自成立，重启证据与 result 一致。Client 组装 drain result 时仍会在持久化失败时把 reason 统一
   覆盖为 `PersistenceFailed`，该优先级是否刻意见 [09](09-delivery-durability-projection-residuals.md) 第 3 条。
3. **Fact/Gap 共用穷尽式 reducer。** 原实现里 Gap 的 non-retryable rejection 既不落死信也不改状态，直接返回
   `Progressed`，于是 pump 立刻重取同一条 Gap——热循环加静默丢失。修法是抽出
   `PendingDeliveryResolution` + `ReducePendingDeliveryAsync`，Fact 与 Gap 走同一套 `Acknowledged / Retry /
   Rejected` 归约，Gap 拒绝落到独立的 `collector-protocol-gap-dead-letter.json`，MessageId 与 GapId 语义分开，
   background 与 drain 两态都有回归。
4. **Dispose 先广播 intent 再聚合等待。** 原实现按快照顺序逐个 await，前一个 release/fence 失败就短路掉后面
   的 Activation，漏发 Stop Intent。修法是新增 `CollectorRuntimeTerminationBatch`：先对快照内所有 InProcess、
   ExternalHost、ManagedProcess lifetime 提交 `RuntimeStopping`，再统一等待全部 Terminal 并聚合异常；重试共享
   同一个 persistent Terminal，不重跑 stop transaction。
5. **ManagedProcess cause 单权威。** 原实现有两处各自推断终止原因，stop 基础设施异常会把已经确定的
   protocol cause 覆盖成 `StopFailed`。修法是把投影集中到 `ManagedProcessTerminationProjector`
   （`Project` / `FromFailedStopReason`），execution 与 logical drain/completion 从同一个 cause 派生；
   误导性的 `FenceAfterDeadline` 改名 `FenceAfterFailedStop` 并显式接收 `CollectorDrainReason`。

### 2026-08-31 — 复审两个 P2 的关闭证据

- ManagedProcess cause 双权威：`ManagedProcessCollectorActivationLifetimeDriver.StopAsync` 的 catch 分支改为
  `client.TerminationCause ?? StopFailed`，回归
  `StopInfrastructureFailureAfterProtocolTerminationPreservesAuthoritativeCause` 断言 `ProtocolFailure` /
  `FlushCancelled` / `CompletionFailed` 三者一致。
- Gap 死信诊断指错文件：`CollectorProtocolOutbox.DeadLetterPath` 在只有 Gap 死信时返回 gap 路径，回归
  `NonRetryableGapRejectionBecomesOneDurableDiagnosticWithoutHotLoop` 断言路径、计数与 `MessageId != GapId`。

两条都先复现为确定性 red，再转 green，不以 commit message 作为证据。

### 2026-08-31 — 最终验证

- `dotnet build Heartbeat.slnx --no-restore -m`：0 warning / 0 error。
- Collector Protocol 40/40、Hub 252/252（含真实 ManagedProcess cross-process）、System Collector 78/78。
- Browser Collector `npm test` 78/78 与 `npm run build` 通过。
- `node scripts/collector-contracts.mjs check --base-ref bad584a5...`：Fact Schema 与 evolution baseline 一致。
- `dotnet format style --diagnostics IDE1006 --verify-no-changes` 与 `git diff --check` 均无输出。
- 最后一次代码修改（`e2e1b93`）之后，期间无任何代码或夹具改动，完整 solution 项目并行连续三轮
  **1033/1033**，每轮分项一致：Server 454、Hub 252、System Collector 78、Desktop.Mac 78、Desktop.Windows 47、
  Protocol 40、Core 31、Desktop.UI 25、VRChat 15、Headless 6、Updater.Velopack 6、Reference.ManagedProcess 1。

原验收写的 `1022/1022` 是过期数字：本轮把一个穷尽式 projector theory 从六个用例合并成一个，同时新增一个
真实 ManagedProcess 回归，真实基数是 1033。按 `docs/agents/engineering-friction.md`「文档与实现不一致随实现
一起修正」的处理方式改写验收文本，而不是删测试去凑历史数字。

### 2026-08-31 — 残留非阻断项与一次夹具 flake

Standards 与 Spec 两轴复审在生产代码上 P1/P2 清零，唯二 P2 是 tracker 文档违规（PRD `## Comments` 不在文件
末尾、本 issue 勾选与实现不一致），已随本次 closeout 修正。生产侧残留判断题不阻断，集中记入
[09 — 死信与 durable projection 残留 P3](09-delivery-durability-projection-residuals.md)。

第二轮 solution 项目并行时 `ManagedProcessCollectorProtocolTranscriptTests.InteractiveAuthorization_DoesNot
ConsumeTheStartupTimeout` 失败一次（`WaitForPhaseAsync` 的 5s 墙钟超时）。该测试不在本轮 diff 内，失败发生在
激活启动等待 `WaitingForAuthorization` 的路径上，而本轮改动只落在终止/drain 与 options 校验；隔离复跑
HEAD 29/30、固定基线 20/20。按环境/夹具类归因，单独记入
[10 — ManagedProcess 授权阶段等待夹具 flake](10-managed-process-authorization-phase-wait-flake.md)，不在本轮
扩大范围去改未触及的旧夹具。随后重跑的三轮为连续全绿。
