# 06 — 全量验证、复审与 lifecycle closeout

Status: done

Owner: Collection / Verification

Priority: P1 — 生命周期竞态只有在真实进程、压力与项目并行下才有可信完成证据。

## Acceptance

- [x] solution build；Protocol/Hub/System suites；Browser test/build；collector contracts；IDE1006；diff check。
- [x] 真实 cross-process crash/drain/restart 与适当 deadline/terminal stress。
- [x] solution 在项目并行下连续多轮通过，从最后一次代码或夹具修复重新计数。
- [x] 最终验证未发现新的无关 flaky gate；后续出现时仍须独立分类和建 issue。
- [x] `code-review` Standards/Spec 双轴独立复审完成，finding 全部关闭。
- [x] PRD/issues 同步 lifecycle；线性 commits、clean worktree、无 push；报告删除量、替换测试、风险与合入建议。

## Comments

### 2026-08-31 — validation and independent review complete

最终代码提交后的证据：solution build 0 warnings / 0 errors；Protocol 36/36、Hub 245/245、System 78/78；
Browser 78/78 且 production build 通过；collector contracts、IDE1006 与 diff check 通过。真实 cross-process
crash/drain/restart 10/10，ManagedProcess deadline 20/20，Hub terminal/deadline/disconnect 七场景各 10 轮，
System lifecycle 四场景各 20 轮，Protocol 全 suite 四轮均通过。完整 solution 在项目并行下从最后一次代码修复
重新计数三轮，每轮 12 个测试项目合计 1022/1022。

Standards 与 Spec 两个独立复审先发现并推动关闭 Gap dirty retry、Ready preparation 越过 deadline、
StopRequested 信号顺序、late cooperative Stop、caller/deadline cancellation 分类与 terminal fence 等问题；
两轴最终 closure review 均确认 P1/P2 cleared。未发现新的无关 flaky gate，无需为本轮另建 issue。
