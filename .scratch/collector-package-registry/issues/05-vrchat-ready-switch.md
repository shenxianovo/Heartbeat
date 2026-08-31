# 05 — VRChat Ready 后切换

Status: ready-for-agent

Owner: Collection / ManagedProcess Driver

Priority: P1 — 新候选在真实 Ready 前不得接管，失败也不得破坏旧 LKG。

## What to build

把 approved exact Installation 接入 VRChat ManagedProcess candidate Activation。新进程独立完成协议协商
并到达 Ready 后即更新成功；Ready 前失败保留旧 LKG，Ready 后退出按普通运行故障处理。

## Acceptance

- [ ] driver 启动批准 offer 绑定的 exact PackageId/version/hash，不再次解析 mutable channel。
- [ ] 启动失败、握手/声明不兼容或 Ready 超时均保留旧 LKG，并把精确 failure reason 投影到 Current。
- [ ] 失败只保存最后错误并等待下一次人工 Approve；不自动重试、不把失败反写成无候选。
- [ ] Ready 后写新 current/LKG 并报告 succeeded；之后退出属于普通运行故障，不触发候选回滚。
- [ ] 一个 Instance 的成功/失败不晋升或回滚共享 Installation 的其他 Instance。
- [ ] host crash/restart 后按已批准 exact ref 重新收敛；Ready 前不得产生双 writer。
- [ ] 旧 LKG 在新候选 Ready 前不会被覆盖；MVP 不实现 cache cleanup。
- [ ] System/BuiltIn 和 ExternalHost 不经过此 adapter。
- [ ] 使用真实子进程 fixture 覆盖 never-ready、ready、ready-then-exit 与 restart-mid-update。

## Dependencies

依赖 issue 04。开发期 MVP 不引入候选稳定窗口：Ready 即视为更新成功（ADR-047）。
