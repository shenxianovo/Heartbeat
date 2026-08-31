# 10 — ManagedProcess 授权阶段等待夹具 flake

Status: done

Owner: Collection / Hub Tests

Priority: P2 — 墙钟夹具超时会在全仓项目并行门禁里产生假失败，削弱「连续三轮全绿」这类验收的可信度。

## Acceptance

- [x] `ManagedProcessCollectorProtocolTranscriptTests.WaitForPhaseAsync` 不再用固定 5s
  `CancellationTokenSource` 轮询真实子进程阶段；改为可确定性等待的信号，或至少把等待预算与真实进程启动
  成本解耦。
- [x] `InteractiveAuthorization_DoesNotConsumeTheStartupTimeout` 不再依赖 `Task.Delay(1_200)` 与
  `StartupTimeout = 1s` 的相对关系来断言「授权不消耗启动超时」。
- [x] 只改测试夹具，不修改生产激活/授权语义（另加两处行为中性的观测/时钟 seam，见下方说明）。
- [x] 精确回归高次重复（≥ 50）与 Hub suite、完整 solution 项目并行连续三轮通过。

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

### 2026-08-31 — 确定性 barrier 收口

commit `91e0211`。

- **Barrier 类型**：Runtime State 发布信号。`CollectorRuntime` 的所有 ManagedProcess 状态写入统一走
  `PublishManagedProcessStateLocked`，发布时完成注册在 `ManagedProcessPhaseWaiter` 上的等待者；测试的
  `WaitForPhaseAsync` 改为 `await runtime.WaitForManagedProcessPhaseAsync(...)`，不再 20ms 轮询。
  剩下的 30s 预算只是 hang guard，超时会报出最后一次发布的 phase 作为诊断，不再与真实子进程启动成本相争。
- **授权超时测试**：两个 `InteractiveAuthorization_*` 用例改用 `StartupTimeProvider` 虚拟时钟。启动预算
  从此只在测试推进虚拟时间时消耗，`Task.Delay(1_200)`/`Task.Delay(2_200)` 全部删除。推进分两步：先走一个
  slice 让启动等待翻过在授权挑战发布前就开始的那一轮，再一次性推进 10 倍启动预算，并等待下一次虚拟
  delay 被排上，证明启动等待在推进后确实重新算过预算。两个用例合计从约 30s 降到约 1s。
- **生产改动说明**：为拿到确定性信号与虚拟时钟，生产侧只加了两处行为中性 seam——上述状态发布观测点
  （internal，仅新增通知，不改变任何 phase 语义），以及把启动超时的 `CancellationTokenSource`、
  `AwaitReadyWithAuthorizationPauseAsync` 的计时接到既有 `CollectorRuntimeOptions.TimeProvider`
  （默认仍是 `TimeProvider.System`）。激活/授权语义、phase 序列与超时判定规则均未改变。

验证：两个授权用例与 issue 09 目标用例合并高次重复 50/50 全绿；Hub suite 255/255；完整 solution 项目
并行连续三轮 1036/1036。
