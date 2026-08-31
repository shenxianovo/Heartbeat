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

## Design It Twice decision

三路设计均由高推理子代理独立完成，主任务逐项核对源码、ADR-040 与 Collection glossary 后比较。

### Design A — 最小 Interface

- Hub：`Start / PublishReady / SubmitStop` 三入口；Client 公共面只保留 `Run`，内部 delivery owner 仅
  `StartBackground / BeginDrain`，能力由 lease 承载。
- **Depth**：最高；caller 只学习极少入口即可获得 deadline、Ready race、terminal result、fence/release。
- **Locality**：Stop 与 delivery policy 各自集中在协议一侧。
- **Seam placement**：正确，位于 Runtime orchestration → Execution Driver，以及 Protocol Client → Binding/outbox。
- 风险：若不保留 typed phase/result，非法 attachment/Ready 中间态可能重新泄漏进参数约定。

### Design B — 最强状态模型

- Hub 与 Client 都使用 sealed phase/result union、generation-bearing ownership lease 与显式 terminal values；
  `Superseded / Fenced / PersistenceFailed` 互不混淆。
- **Depth**：行为 leverage 很高，但 Interface 方法与类型最多。
- **Locality**：最强；Ready publication、Stop Intent、admission、handoff、commit reducer 全部只有一个权威。
- **Seam placement**：正确；没有跨进程 owner。
- 风险：公开 `Phase`、通用 `Attach/Fence/Complete` 会让 caller 学到过多实现阶段，增加 shallow misuse surface。

### Design C — 最易迁移

- lifetime 在 accepted Hello 时创建；保留现有 activation `StopAsync` 作为 Stop Intent façade，Runtime、update、
  cleanup、supervision 逐一改为同一 owner；Client 只主动破坏 `StopAsync` 参数为 drain-scoped context。
- **Depth**：接近 A，现有 caller 改动最小。
- **Locality**：owner 集中，但若保留现有 wrapper 的 `State/DrainResult` 可变字段会形成第二权威。
- **Seam placement**：最贴合当前代码的原子替换点。
- 风险：迁移便利不能成为保留旧 coordinator 的理由；每个 slice 必须同改同删。

### Recommended hybrid

采用 A 的小 Interface、B 的 typed ownership/result、C 的 seam placement。Hub Module 定名
`CollectorActivationLifetime`，在 accepted Hello 或 ExternalHost reservation 时创建并一次附着 Driver、
session fence、Ready publication 与 release dependencies：

```csharp
internal sealed class CollectorActivationLifetime
{
    public CancellationToken StopRequested { get; }
    public Task<CollectorActivationTerminalResult> Terminal { get; }

    public ValueTask<CollectorReadyOutcome> PublishReadyAsync(
        CollectorReadyPublication publication,
        CancellationToken waitCancellation = default);

    public ValueTask<CollectorActivationTerminalResult> RequestStopAsync(
        CollectorActivationStopIntent intent,
        CancellationToken waitCancellation = default);
}
```

- `Terminal` 在 owner 创建时分配一次，永不清空；operational failure 是 terminal result value，只有实现不变量
  破坏才 fault task。
- 第一个 Stop Intent 固定 winning cause 与一个绝对 deadline；后续 intent 只进 diagnostics，并返回同一 task。
- `waitCancellation` 只包裹 `Terminal.WaitAsync`，不进入 terminal transaction。
- Ready preparation 在锁外；最终 durable publication/writer grant 与 Stop Intent 在同一短 gate 线性化。
  Stop 赢则 Ready 返回 `Stopping` 且 writer 从未发布；Ready 赢则同一 owner 最终 fence/release。
- Stop policy 属于 Implementation/Driver adapter：InProcess 在同一 deadline 内最多两次 cooperative attempt，
  之后 fence；ManagedProcess protocol drain 失败或到期即 terminate；ExternalHost 只 revoke lease，不声明进程
  已停止或 fully drained。任何后续 caller 都不能触发新 attempt。
- `InProcessCollectorActivation.StopAsync` 可暂保 source-compatible façade，但只能提交 Intent/等待同一 Terminal；
  不得缓存 task、执行 Driver Stop、推进 fence 或回调 Runtime release。

Client 内部 Module 定名 `CollectorDeliveryOwnership`：

```csharp
internal sealed class CollectorDeliveryOwnership
{
    public CollectorDeliveryLease BeginBackground();
    public CollectorDrainTransition BeginDrain(CollectorDrainRequest request);
}

internal sealed class CollectorDeliveryLease
{
    public ValueTask<CollectorAdmissionOutcome> AdmitAsync(
        CollectorOutboxMutation mutation,
        CancellationToken operationCancellation = default);

    public ValueTask<CollectorDeliveryStepResult> DeliverNextAsync(
        CancellationToken operationCancellation = default);
}
```

- `BeginDrain` 在一个短 transition 中关闭 ordinary admission、supersede background lease、推进 commit epoch、
  转移给 drain lease，并原样捕获 Hub 提供的绝对 deadline；网络、Collector 与文件准备都在锁外。
- `CollectorDrainTransition` 还携带 drain-tail admission；`ICollectorProtocolApplication.StopAsync` 改收
  `CollectorDrainContext`，只能把 drain 开始前已截断的 Collector Ingress Queue tail 持久化。Stop 返回后
  tail admission seal，再由 drain lease final flush。
- Fact/Gap 使用同一 `PendingDelivery`/step reducer。ACK 或 durable replacement 返回
  `Committed / Superseded / Fenced / PersistenceFailed`；Superseded 不再抛 `OperationCanceledException`。
- caller/transport cancellation 仍是 cancellation；deadline、persistence、Stop、flush、completion failure
  继续生成各自真实 result。Fenced snapshot 后不允许 late ACK 或 late durable mutation。

拒绝把强状态案的通用 `Phase/Fence/Complete` 暴露给 caller，也拒绝把 Runtime-wide actor 或跨进程状态机
作为 owner；前者扩大 Interface，后者违反 seam 与 ADR-040。

## Migration boundary

- replace：`StartingCollector`、Activation 内部 stop-task/retry coordination、Runtime 各处 direct Stop 与 Client
  handoff observer/barrier 被目标 Module 接管后删除。
- preserve：协议 wire shape、Fact/Gap outbox、Hub projection、Artifact Delivery/Registry、Package/Instance/Activation
  身份、三种 Execution Driver 能力差异。
- compatibility：本任务不新增落盘格式兼容分支；若状态格式必须变化，需单独记录服务对象与退出条件。

## Exit conditions

- [x] Design It Twice 至少三案完成，按 Depth、Locality、seam placement 比较，推荐 Interface 写入本 PRD。
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
- [x] [02 — Design It Twice 并冻结生命周期 Interface](issues/02-design-lifecycle-interface.md)
- [ ] [03 — Hub 侧单一 Activation Lifetime owner](issues/03-hub-activation-lifetime.md)
- [ ] [04 — Client 侧显式 Delivery Ownership](issues/04-client-delivery-ownership.md)
- [ ] [05 — 三种 Execution Driver conformance](issues/05-execution-driver-conformance.md)
- [ ] [06 — 全量验证、复审与 lifecycle closeout](issues/06-validation-and-review.md)

## Non-goals

- 不实现 Collector Package Registry、Artifact Delivery 或新的安装/升级产品行为。
- 不改变 Fact Schema、Analytics ingest、Dashboard 或 Collector wire protocol。
- 不把 Hub 与 Client owner 合成跨进程状态机。
- 不用更多 sleep、宽松 catch 或 pending=0 覆盖真实失败。
