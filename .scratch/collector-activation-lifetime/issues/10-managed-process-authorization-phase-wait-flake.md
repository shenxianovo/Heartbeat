# 10 — ManagedProcess 授权阶段等待夹具 flake

Status: ready-for-agent

Owner: Collection / Hub Tests

Priority: P2 — 墙钟夹具超时会在全仓项目并行门禁里产生假失败，削弱「连续三轮全绿」这类验收的可信度。

## Acceptance

- [ ] `ManagedProcessCollectorProtocolTranscriptTests.WaitForPhaseAsync` 不再用固定 5s
  `CancellationTokenSource` 轮询真实子进程阶段；改为可确定性等待的信号，或至少把等待预算与真实进程启动
  成本解耦。
- [ ] `InteractiveAuthorization_DoesNotConsumeTheStartupTimeout` 不再依赖 `Task.Delay(1_200)` 与
  `StartupTimeout = 1s` 的相对关系来断言「授权不消耗启动超时」。
- [ ] 只改测试夹具，不修改生产激活/授权语义。
- [ ] 精确回归高次重复（≥ 50）与 Hub suite、完整 solution 项目并行连续三轮通过。

## Comments

### 2026-08-31 — evidence from third-round closeout gate

在固定 Collector diff（`bad584a5...e2e1b93`）的完整 solution 项目并行第 2 轮中失败一次：

```
ManagedProcessCollectorProtocolTranscriptTests.InteractiveAuthorization_DoesNotConsumeTheStartupTimeout [FAIL]
System.Threading.Tasks.TaskCanceledException : A task was canceled.
   at ...WaitForPhaseAsync(CollectorRuntime, Guid, CollectorRuntimePhase) line 921
   at ...InteractiveAuthorization_DoesNotConsumeTheStartupTimeout() line 506
```

归因证据：

- 该测试与 `WaitForPhaseAsync` 都**不在**本轮 diff 内（`git diff bad584a5...HEAD` 对二者零改动）。
- 失败点是等待激活进入 `WaitingForAuthorization`，属启动路径；本轮生产改动只落在终止/drain 投影与
  options 时长校验，不触碰启动或授权挑战。
- 隔离复跑：HEAD 29/30 通过（失败那次耗时正好 5s，即夹具超时；通过时 1–2s），固定基线 20/20 通过。
  样本量不足以证明是本轮引入，但足以证明它对机器负载与真实进程启动抖动敏感。

结论按 `docs/agents/engineering-friction.md` 归入测试/环境类，不在 07 的范围内修改未触及的旧夹具。
重跑后的三轮连续 1033/1033 全绿。
