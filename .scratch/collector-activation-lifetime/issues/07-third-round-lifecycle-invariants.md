# 07 — 第三轮生命周期不变量收口

Status: ready-for-agent

Owner: Collection / Collector Runtime + Protocol

Priority: P1 — Terminal、durable ownership、pending delivery、Dispose fan-out 与 ManagedProcess cause 仍存在会
产生永久等待、错误终态、热循环或漏发 Stop Intent 的路径。

## Acceptance

- [ ] 每个 `CollectorActivationLifetime.Terminal` 在任意路径只完成一次；普通 operational failure 返回 terminal
  value，不变量/基础设施异常 fault Terminal；覆盖超大正 drain budget、`TimeProvider/CreateTimer` 异常与 caller
  wait cancellation，并收紧公开 options 验证。
- [ ] `RemainderDurable` 只由真实 durable ownership 推导；覆盖 ordinary admission 首次持久化 `IOException`、
  `BeginDrain` supersede、final flush 卡到 deadline 的组合，重启证据与 result 一致，persistence/deadline/flush
  原因可区分。
- [ ] Fact/Gap 共用穷尽式 pending-delivery reducer；non-retryable Gap rejection 不热循环、不静默丢失，具有明确、
  可持久、可诊断的有限状态/结果；background 与 drain、messageId/GapId 语义、no late commit 均有回归。
- [ ] Runtime Dispose 先向快照中的所有 InProcess、ExternalHost、ManagedProcess lifetime 提交
  `RuntimeStopping` intent，再等待所有 Terminal 并聚合错误；单个 release/fence failure 不阻止其他 Activation，
  重试共享 persistent Terminal 且不重跑 stop transaction。
- [ ] ManagedProcess termination cause 只有一个权威；`DrainWriteFailed`、`DeadlineExceeded`、`BeforeReady`、
  `StopFailed`、`ProtocolFailure` 的 execution 与 logical drain/completion projection 一致。
- [ ] InProcess/ExternalHost Ready prepare/commit、durable publication、writer grant 的重复只在不扩大公共接口、
  不引入跨进程 owner 且明显减少权威来源时收敛；否则记录非阻断 follow-up。
- [ ] 保持 wire shape、Fact Schema、outbox 落盘兼容、Package/Instance/Activation 身份、ExternalHost 弱能力及
  Artifact Delivery/Registry 范围不变；不恢复已退役 coordinator/fence。
- [ ] 五组确定性回归逐组先红后绿；Protocol/Hub/System、Browser test/build、collector contracts、IDE1006、
  diff check、真实 ManagedProcess/cross-process 通过；最后一次代码修改后完整 solution 项目并行连续三轮
  1022/1022。
- [ ] 两个隔离的高推理 subagent 对 `bad584a5fbd8f93e64eabd9068b4b34386e142c5...HEAD` 完成 Standards/Spec
  双轴复审，P1/P2 清零；提交线性、worktree clean、无 push。

## Comments

### 2026-08-31 — opened from fixed-base review

本 issue 承接第三轮架构收口。测试 seam 继续使用 PRD 已冻结的 Hub lifetime、Client delivery ownership、
Execution Driver conformance 与 Collector Protocol conformance；不新增跨进程 owner 或公共 lifecycle surface。
