# 06 — 全量验证、复审与 lifecycle closeout

Status: ready-for-agent

Owner: Collection / Verification

Priority: P1 — 生命周期竞态只有在真实进程、压力与项目并行下才有可信完成证据。

## Acceptance

- [ ] solution build；Protocol/Hub/System suites；Browser test/build；collector contracts；IDE1006；diff check。
- [ ] 真实 cross-process crash/drain/restart 与适当 deadline/terminal stress。
- [ ] solution 在项目并行下连续多轮通过，从最后一次代码或夹具修复重新计数。
- [ ] 无关 flaky gate 按产品/测试/环境分类并建立独立 issue。
- [ ] `code-review` Standards/Spec 双轴独立复审完成，finding 关闭或明确阻塞。
- [ ] PRD/issues 同步 lifecycle；线性 commits、clean worktree、无 push；报告删除量、替换测试、风险与合入建议。

