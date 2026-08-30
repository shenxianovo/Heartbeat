# 05 — 接入 ManagedProcess 的候选稳定与 LKG

Status: ready-for-agent

Owner: Collection / ManagedProcess Driver

Priority: P1 — Ready 和更新成功之间必须保留真实稳定观察与可恢复旧版本。

## What to build

把 approved exact Installation 接入 ManagedProcess candidate Activation。新进程先独立完成协议协商与
Ready，再经过 per-Instance stability period；期间失败回滚该 Instance 的 LKG，成功后才晋升。

## Acceptance

- [ ] driver 启动批准 offer 绑定的 exact PackageId/version/hash，不再次解析 mutable channel。
- [ ] 启动失败、握手/声明不兼容、Ready 超时、stability period 内退出均保留/恢复旧 LKG，并把精确
  failure reason 投影到 Current。
- [ ] Ready 只表示可以承担 stream；只有 stability period 完成才写新 LKG 和 succeeded。
- [ ] 一个 Instance 的成功/失败不晋升或回滚共享 Installation 的其他 Instance。
- [ ] host crash/restart 后能从 durable transaction state 判定继续观察、回滚或回到 awaiting approval，
  不产生双 writer。
- [ ] 旧 LKG 在新候选晋升前不会被 cache cleanup 删除；晋升后仍按明确保留策略可诊断回退。
- [ ] System/BuiltIn 和 ExternalHost 不经过此 adapter。
- [ ] 使用真实子进程 fixture 覆盖 never-ready、ready-then-crash、stable、restart-mid-update 与 rollback。

## Dependencies

依赖 issue 04 与现有候选稳定窗口语义。
