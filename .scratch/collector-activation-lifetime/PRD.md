# Collector Activation 生命周期所有权收敛

Status: ready-for-agent

## Problem

Collector Activation 的 Hub 侧终止所有权散落在 `CollectorRuntime.Dispose`、activation failure cleanup、
`StartingCollector`、`InProcessCollectorActivation`、ManagedProcess supervision/update 与 ExternalHost revoke；
Client 侧 background pump、drain、deadline fence、final flush 与 completion 又分别推进 admission、delivery epoch
和 durable commit fence。多个调用者可以直接执行 Stop、消费同一次异常、重置 `_stopTask` 或释放 writer，
因此最终结果会依赖“谁先 await”，测试也被迫观察内部调度顺序。

已知门禁 `RuntimeDispose_StartingCollectorStopFailureReturnsSoDisposeCanBeRetried` 还把一次共享 Stop failure
假设为一定由 Dispose 消费；全仓并行时 activation cleanup 可以先消费失败，随后 Dispose 成功。这是夹具
竞态，不是应通过修改生产重试语义来迎合的行为。

## Outcome

- Hub 与 Collector 两侧各有一个唯一生命周期 owner，不形成跨进程巨无霸。
- Hub 侧一个深 Module 吸收 Starting、Ready、Stop、Dispose、failure cleanup、deadline 与 writer release；
  Runtime、update、deactivate、supervision 只提交 Stop Intent。
- 同一 Activation 的所有终止调用者共享一个持久 terminal task/result；调用者 cancellation 只取消等待，
  不取消终止事务。Stop failure、重试与最终错误策略只由 Module 决定。
- Client 侧 background → drain → fenced 是显式 delivery ownership 转移；Superseded epoch/handoff 是领域结果，
  不再伪装成 `OperationCanceledException`。
- admission close、delivery ownership transfer、commit epoch advance 与绝对 deadline capture 在一个线性化
  transition 中完成；Collector/transport/persistence 等长操作都在锁外。
- 旧内部调度测试由新 Module Interface 测试替换；迁移完成的 coordinator 被删除，不保留双层 ownership。

## Existing ownership map at `e45bf964a42ee42585d9ec7922f5c76bddc8a16a`

| Path | 现在可以做什么 | 泄漏的 ownership / 竞态 |
| --- | --- | --- |
| `CollectorRuntime.DisposeCoreAsync` | 枚举 Starting/InProcess/ManagedProcess/ExternalHost 并逐个直接 Stop | Runtime 自己执行终止；失败后 `_disposeTask = null`，下一 caller 隐式重试整批 |
| `ActivateProtocolAsync` failure cleanup | 直接 Stop activation 或 StartingCollector，并决定是否移除 `_startingInstances` | 与 Runtime Dispose 竞争同一失败、异常消费与 ownership release |
| `StartingCollector` | 取消 initialize/streams callback，选择 collector/activation Stop，推进 fence，完成 `ActivationCompleted` | 自己持 `_stopTask` 且失败后置 null；同时承担 start coordination、deadline 与 terminal policy |
| `InProcessCollectorActivation` | BeginDrain、调用 Collector Stop、deadline fence、设置 DrainResult、调用 Runtime `CompleteStop` | 第二份 `_stopTask = null` 重试策略；activation 对 Runtime 内部字典/writer release 有反向调用 |
| `ManagedProcessCollectorActivation` | 第三份 stop task，先停 protocol activation，再汇总 process drain；supervision 也可 Stop | update、Dispose、unexpected exit 争抢 termination；失败后重置 stop ownership |
| ExternalHost Runtime paths | `StopExternalHostActivation` / `AbandonExternalHostActivation` 直接 CompleteStop、移除 writer | revoke 与 pending/ready cleanup 没有共享 terminal result |
| `CollectorProtocolClient.RunAsync` | background pump、application cancellation、Stop、final flush、completion 全在一个方法中编排 | admission bool、handoff bool、commit epoch 与 deadline 分步推进，中间状态可见 |
| `CollectorDeliveryCommitFence` + catch filters | 以 epoch mismatch 抛 `OperationCanceledException` 表达 handoff | Superseded 与 caller/deadline cancellation 共用异常通道；测试需 internal observer/barrier |

## Target invariants

1. 每个 Hub Activation 只有一个 lifetime owner；Stop Intent 可重复提交，但 terminal transaction 只创建一次。
2. terminal task/result 对 Activation lifetime 持久，不因失败、取消等待或某个 caller 观察异常而被清空。
3. deadline 是 terminal/drain transition 捕获的绝对 instant；所有等待共享它，不能逐 caller 延长。
4. Ready 与 Stop Intent 线性化：Stop 赢则 Ready 不能发布 writer；Ready 赢则同一 owner 负责 drain/release。
5. writer、durable commit gate 与 ownership 只由 terminal transaction 释放一次；failure cleanup 不另开旁路。
6. Client drain transition 原子关闭 admission、转移 delivery ownership、推进 commit epoch并固定 deadline。
7. Superseded delivery 是显式 ownership outcome；真实 caller cancellation、deadline、persistence、Stop、
   completion failure 保持可区分。
8. 保持 no late ACK、no late durable mutation、bounded drain、truthful remainder 与 callback 不同步等待持久化。
9. InProcess、ManagedProcess、ExternalHost 共享生命周期 Interface，但 adapter 如实保留 stop/terminate/revoke
   能力差异；Package/Instance/Activation、单一 Collector Protocol、多 Binding 继续遵守 ADR-040。

## Agreed test seams

TDD 只在以下 Interface seam 写新测试，旧测试迁移后删除或替换：

- Hub lifetime Interface：start attachment、Ready publication、Stop Intent、terminal result。
- Client delivery ownership Interface：admission、background delivery lease、drain transition、delivery commit outcome。
- Execution Driver adapter conformance：InProcess bounded stop/fence、ManagedProcess terminate、ExternalHost revoke。
- Collector Protocol conformance：logical drain result 与 completion result，不观察内部 task 调度。

最终 Interface 名称与方法形状必须先完成 Design It Twice 比较并写回本记录，才能进入生产实现。

## Migration boundary

- replace：`StartingCollector`、Activation 内部 stop-task/retry coordination、Runtime 各处 direct Stop 与 Client
  handoff observer/barrier 被目标 Module 接管后删除。
- preserve：协议 wire shape、Fact/Gap outbox、Hub projection、Artifact Delivery/Registry、Package/Instance/Activation
  身份、三种 Execution Driver 能力差异。
- compatibility：本任务不新增落盘格式兼容分支；若状态格式必须变化，需单独记录服务对象与退出条件。

## Exit conditions

- [ ] Design It Twice 至少三案完成，按 Depth、Locality、seam placement 比较，推荐 Interface 写入本 PRD。
- [ ] Hub vertical slice 通过 concurrent Dispose/failure cleanup、Stop failure policy、deadline、Ready race 的
  Interface 级测试，旧调度测试已替换。
- [ ] Client vertical slice 统一 Fact/Gap、cooperative/deadline/failure；observer/barrier production hook 已删除。
- [ ] 三种 Execution Driver conformance/adapter 验证通过且不伪装 ExternalHost 能力。
- [ ] build、Protocol/Hub/System、Browser test/build、collector contracts、style、diff、真实 cross-process、
  stress 与 solution 项目并行连续多轮通过。
- [ ] 新的无关 flaky gate 已按产品/测试/环境分类并单独建 issue，不扩张本 feature。
- [ ] Standards/Spec 双轴独立复审完成，P1/P2 清零；线性 commits、clean worktree、无 push。

## Required work

- [x] [01 — 固定已知 Runtime Dispose 测试夹具竞态](issues/01-fix-runtime-dispose-fixture-race.md)
- [ ] [02 — Design It Twice 并冻结生命周期 Interface](issues/02-design-lifecycle-interface.md)
- [ ] [03 — Hub 侧单一 Activation Lifetime owner](issues/03-hub-activation-lifetime.md)
- [ ] [04 — Client 侧显式 Delivery Ownership](issues/04-client-delivery-ownership.md)
- [ ] [05 — 三种 Execution Driver conformance](issues/05-execution-driver-conformance.md)
- [ ] [06 — 全量验证、复审与 lifecycle closeout](issues/06-validation-and-review.md)

## Non-goals

- 不实现 Collector Package Registry、Artifact Delivery 或新的安装/升级产品行为。
- 不改变 Fact Schema、Analytics ingest、Dashboard 或 Collector wire protocol。
- 不把 Hub 与 Client owner 合成跨进程状态机。
- 不用更多 sleep、宽松 catch 或 pending=0 覆盖真实失败。
