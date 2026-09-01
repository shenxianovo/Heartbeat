# 05 — VRChat Ready 后切换

Status: done

Owner: Collection / ManagedProcess Driver

Priority: P1 — 新候选在真实 Ready 前不得接管，失败也不得破坏旧 LKG。

## What to build

把 approved exact Installation 接入 VRChat ManagedProcess candidate Activation。新进程独立完成协议协商
并到达 Ready 后即更新成功；Ready 前失败保留旧 LKG，Ready 后退出按普通运行故障处理。

## Acceptance

- [x] driver 启动批准 offer 绑定的 exact PackageId/version/hash，不再次解析 mutable channel。
      `CollectorPackageSwitch` 只把已批准的三元组交给 `CollectorInstallationStore.OpenInstallation`，
      拿到的 Installation 目录就是候选进程的工作目录；没有第二次 Registry 读取。
- [x] 启动失败、握手/声明不兼容或 Ready 超时均保留旧 LKG，并把精确 failure reason 投影到 Current。
      Runtime 的 Activation failure code 映射到既有 `CollectorRegistryFailureReason`（新增
      `ReadyTimeout` / `StartupFailed` / `Incompatible`），原始 code 保留在 detail 里。
- [x] 失败只保存最后错误并等待下一次人工 Approve；不自动重试、不把失败反写成无候选。
- [x] Ready 后写新 current/LKG 并报告 succeeded；之后退出属于普通运行故障，不触发候选回滚。
- [x] 一个 Instance 的成功/失败不晋升或回滚共享 Installation 的其他 Instance。
- [x] host crash/restart 后不产生双 writer，并按持久化的 Collector Runtime State 收敛。
      **与原文的偏差**：收敛目标是"已经到达过 Ready 的那份 Package"，不是"已批准的 exact ref"。
      见下方 Comments。2026-09-01：crash 与 restart 现在分别有用例，
      `SwitchToApproved_HostCrashesBeforeReady_ConvergesOnTheEffectivePackageWithOneWriter`
      不走任何优雅停止路径。
- [x] 旧 LKG 在新候选 Ready 前不会被覆盖；MVP 不实现 cache cleanup。
      LKG 现在只在候选 Ready 之后写一次，切换前不再预写旧 LKG。
- [x] System/BuiltIn 和 ExternalHost 不经过此 adapter。切换只有一条实现路径
      `CollectorRuntime.UpdateManagedProcessAsync`，这两种 driver 没有入口。
- [x] 使用真实子进程 fixture 覆盖 never-ready、ready、ready-then-exit 与 restart-mid-update；
  restart-mid-update 含两种：取消后重启，以及宿主在候选 starting 时**崩溃**（不 Stop、不 Dispose、不写任何
  关闭期状态）后重启。

## Dependencies

依赖 issue 04。开发期 MVP 不引入候选稳定窗口：Ready 即视为更新成功（ADR-047）。

## Comments

- 2026-08-31：实现完成，全部由自动化测试覆盖。链路是
  `POST /hub/api/v1/collector-instances/{id}/package-update/switch` → `HeadlessFleetManager`
  → `CollectorPackageSwitch` → `CollectorRuntime.UpdateManagedProcessAsync`。
- 2026-08-31：**没有新增平行更新路径**。既有 `UpdateManagedProcessAsync` 被改造成 Ready 即成功：
  删除 `ManagedProcessUpdateOptions.StabilityPeriod` 与"窗口内退出即回滚"的语义，Last-Known-Good 从
  "切换前写旧、窗口后写新"改成"只在候选 Ready 之后写新"。Ready 之后的退出交给 supervision 报运行故障。
- 2026-08-31：**restart 语义的裁决**。Acceptance 原文写"按已批准 exact ref 重新收敛"，实现改为按
  effective Package 收敛：只有已经到达过 Ready 的候选才会在重启后被启动，从未 Ready 的已批准候选仍然
  由 host 交付的 Package（当前即旧 LKG）继续跑。理由是 ADR-047 只承认 Ready 为成功；若重启能让未 Ready
  的候选接管，重启就成了绕过 Ready 的第二条晋升路径，而且 Ready 前的失败会在每次重启后重放。批准仍然
  保留，下一次切换由人再次触发。`CollectorPackageSwitch.ResolveEffectivePackage` 是这条规则的唯一实现，
  Headless 启动时按它决定 Entry 的 Package。
- 2026-08-31：切换是与批准分开的一次 owner 动作（新增 `/package-update/switch`），批准不再隐含"下次
  重启就会用"。已在 issue 07 的手工 smoke 步骤里补一行。
- 2026-08-31：Ready 之后的候选退出不再回滚，因此"候选 Ready 后立刻挂掉"的 Instance 会停在普通运行失败
  状态等 supervision/owner 处理；这是 ADR-047 的取舍，不是漏掉的回滚。
- 2026-08-31：测试证据（`dotnet test Heartbeat.slnx`，1219 passed / 0 failed）：
  - `CollectorPackageSwitchTests`（27 个，真实 ManagedProcess 子进程 + 真实 Installation 目录）：Ready
    晋升、AlreadyCurrent 幂等、无批准即无动作、never-ready → `ReadyTimeout`、非零退出 →
    `StartupFailed`、握手不兼容 → `Incompatible`、完成标记缺失/指向别的候选 → 在停旧 Activation 之前被
    拒、共享 Installation 的另一个 Instance 不被连带晋升或记失败、并发第二次切换被拒且不产生第二个
    writer（重复 20 次，用 phase barrier 而不是 sleep 保证确定性）、重启后按 effective Package 收敛、
    effective Installation 丢标记则回落并记原因、Ready 前被取消后重启仍跑旧 Package、failure reason
    映射表。
  - `VRChatManagedProcessCollectorTests.PackageSwitch_ApprovedInstallationReachesReady_TakesOverWithoutReauthorizing`：
    真实 VRChat Collector 子进程（mock API）完成两步认证后切到已安装的下一个版本，候选靠 per-Instance
    密钥恢复会话直接到 Ready，无需重新授权，Fact Stream 身份不变。
  - `ManagedProcessCollectorProtocolTranscriptTests`：Ready 即晋升且不观察窗口、never-ready 回落、
    Ready 后退出仍算成功并由 supervision 报 `process_exited`。
  - `HeadlessManagementApiTests`：`/switch` 与其他管理端点同在既有 owner 授权组内，未认证 401、
    非本 Hub 的 Instance 404。
- 2026-08-31：本 issue 不新增人工门禁；真实 VRChat 端到端 smoke 仍是 issue 07 的门禁。

### 2026-09-01 — 双轴复审收口

- **P2「restart-mid-update 只有优雅取消，没有真正的 crash」已清**：新增
  `CollectorPackageSwitchTests.SwitchToApproved_HostCrashesBeforeReady_ConvergesOnTheEffectivePackageWithOneWriter`
  （重复 **20** 次，用既有 phase barrier 卡在候选 `Starting`，不用 sleep）。它在候选 mid-start 时丢弃整个
  Runtime：不调用 `StopAsync`、不 `Dispose`、不取消，然后由重启后的宿主重新 `ResolveEffectivePackage` 并激活。
  断言：`LastFailure` 与 `LastKnownGood` 都是 null（崩溃的宿主根本没机会写回滚记录，这正是它和"取消后重启"
  用例的区别）、`CurrentVersion` 仍是旧版本、批准与 Installation 都还在、resolve 结果是 host 交付的 Package、
  重启后的 Activation 到达 Ready 且**沿用同一个 Fact Stream**（没有第二个 writer）。反向验证：把
  `ResolveEffectivePackage` 改成无条件返回已批准候选，该用例立刻失败。
- **测试基建限制（写明而不是绕过）**：Collector Runtime State 的 ownership lock 只有真实进程死亡才会释放，
  同进程内第二次 `CollectorRuntime.Open` 一定被拒（这是 `Open_SameStateFileAlreadyOwned_RejectsConcurrentRuntime`
  钉住的正确行为）。因此 fixture 的 `CrashHost()` 让重启后的宿主打开**崩溃瞬间状态目录的副本**（不含 lock 与
  半写的 `.tmp`）——崩溃的语义"关闭期不写任何东西"因此是精确的，被建模的只有那把锁。被丢弃的宿主在全部断言
  之后才被取消并回收，纯粹是为了归还子进程，它写的目录已经没有任何读者。
- 验证：`dotnet build Heartbeat.slnx --no-restore -c Debug` → 0 Warning / 0 Error；
  该用例定向重复 20 次通过；`dotnet test Heartbeat.slnx --no-build` → 1223 passed / 0 failed
  （基线 1219 + 新增 4）。Status 保持 `done`。
- **ADR-047 已同步 restart 收敛规则**（原先只记在本 issue 与 `collection/CONTEXT.md`）：正文补上"宿主重启按
  effective Package 收敛"及其理由——避免重启成为绕过 Ready 的第二条晋升路径、避免 Ready 前的失败每次重启被
  重放。同日也把"管理面共四个 owner 动作、Approve 不隐含接管、Switch 才触发候选启动"写进 ADR-047 与 PRD。
