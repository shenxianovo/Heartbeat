# 03 — 让 InProcess drain 受 deadline 约束

Status: done

Owner: Collection / Collector Protocol

Priority: P1 — 应用 Stop 若不返回，Runtime 永远无法报告 remaining facts/gaps 或完成宿主退出。

## What to build

让 drain request 的 deadline 覆盖 application stop、adapter pump flush 和 drain completion 整个过程。
InProcess System adapter 与通用 client 使用同一有界语义：到期后保存可恢复状态并返回真实 remainder，
不得以 `CancellationToken.None` 进入无界 stop/retry。

## Acceptance

- [x] 收到 drain 后立即根据绝对 deadline 创建 token；`application.StopAsync`、pump flush、outbox flush
  与 completion 都受该 deadline 或更短局部预算约束。
- [x] cooperative stop 正常完成；hung/ignoring-cancellation application 到期后 Runtime 仍在有界时间返回，
  并报告 durable pending fact/gap counts，或 truthful unknown/non-durable remainder。
- [x] deadline 过去、stop 抛错、flush 取消、binding completion 失败各有稳定 runtime reason；不得宣称
  fully drained。
- [x] InProcess adapter 不存在 deadline 外无限 retry；宿主退出后 restart 能重放 durable remainder。
- [x] fake-clock/controlled-task tests 覆盖 stop-before-deadline、stop-at-deadline、never-stop、pending facts/
  gaps、completion failure 与 restart replay，且不会留下后台 task/双 writer。
- [x] ManagedProcess/ExternalHost 现有 drain transcript 不回归；共用 conformance fixture 对三种 driver 的
  deadline/result 断言一致。

## Comments

### 2026-08-30 — 实现前审计暂停，等待 owner 裁决

代码事实与当前 acceptance 在四处存在真实语义冲突，不能只给现有 await 添加 cancellation token：

1. System ingress channel 中尚未进入 Collector outbox 的 Segment/InputEvent tail 不是 durable
   remainder。当前 `PublishAsync` 把本地持久化与远端 flush 耦合，首个 backpressure 会阻止后续 tail
   stage。deadline 到期时必须决定：拆出 durable stage 后 restart replay，还是把未 stage tail 原子转换
   为精确 Stream Gap；只返回 count 会虚构可恢复性。
2. 现有 InProcess stop failure 语义保留 writer 以允许 retry；bounded host shutdown 则必须 fence admission
   并释放 writer，防止双 writer。需要明确普通显式 stop failure 与 Runtime shutdown deadline 是否采用
   不同规则。hung application 无法被进程内强杀，release 后只能依赖 session fence 拒绝 late publish。
3. ExternalHost 按 ADR-040 只有 lease revoke，Hub 当前没有向 host 发起 drain request/deadline；
   `activation.drained` 只接收并丢弃 remainder。需要决定“一致性”仅共享 outcome 词汇并保留弱 lease
   语义，还是扩展 HTTP binding 支持 Hub drain directive。
4. outbox 磁盘持续失败时，bounded return 与 durable remainder 无法同时保证。需要允许稳定的
   non-durable/unknown remainder 结果，或明确磁盘恢复是 shutdown 的外部门禁，不能仍宣称 fully
   drained。

另外审计确认：Client 在注册 drain waiter 前先执行初始 outbox flush，持续 Retry/持久化失败会使它永远
看不到 drain；收到 drain 后 `application.StopAsync` 与 completion 使用 `CancellationToken.None`；System
pump final drain 也用 None。ManagedProcess 的 drain write 与 kill 后 wait 仍可漂移出绝对 deadline，超时
终止目前被记为普通 Stopped；completion failure 不能诚实地写进未送达的 `activation.drained` body，
因此结果模型至少要分 Collector logical outcome 与 Runtime completion outcome 两层。

上述裁决前未保留探索性代码或失败测试，工作树回到已验证的 A3 HEAD。裁决后建议以单一绝对 deadline
request、application lifetime cancellation、client/outbox admission fence 和两层 drain result 为最小
实现 seam，再补 controlled-task/fake-time 与三 driver conformance vectors。

### 2026-08-30 — owner 裁决，恢复实现

Owner 确认采用推荐语义：System ingress 先进入 durable stage，再异步 remote flush；普通 stop failure
保留 writer 供 retry，但 shutdown deadline 到期必须 fence admission 并 release；ExternalHost 保留
ADR-040 的 lease-revoke 弱语义，只共享 outcome 词汇，不在本 issue 新增 Hub→host drain directive；
永久磁盘失败允许 `non-durable/unknown remainder`，绝不宣称 fully drained。结果模型分 Collector
logical outcome 与 Runtime completion outcome，`completion_failed` 不伪装成已送达的
`activation.drained` 内容。

### 2026-08-30 — 实现与验证完成

实现以同一个绝对 deadline 收口 client application stop、System pump、final outbox flush 与 completion；
所有可能忽略 cancellation 的最终 await 都有外层 deadline wait。System callback 在返回前先 append+fsync
到实例级 ingress journal，Protocol outbox durable accept 后才 prefix compact；因此 deadline/crash 可从
ingress journal、outbox 或 Hub durable state 的 Fact identity union 恢复，未知 ACK 不会被重复计数或删账。
Hub deadline 在释放 writer 前同步 hard-fence System client；普通 returned `stop_failed` 视为 Collector 已
fence 的逻辑结果，真实 thrown Stop failure 仍保留 writer 供 retry。Starting Collector、ManagedProcess kill/
wait 与 Runtime dispose 均有界；ExternalHost 只保存 logical/completion outcome 并 revoke lease。

结果模型已分 Collector logical outcome 与 Runtime completion outcome，ManagedProcess snapshot 与
ExternalHost Activation 可查询该结果。共享 conformance corpus 不只校验词汇：InProcess、ManagedProcess、
ExternalHost 的真实行为测试分别验证 `fence_and_release`、`terminate_and_release`、`revoke_lease` 后读取
对应 driver row。

验证：`dotnet build Heartbeat.slnx --no-restore --configuration Debug` 为 0 warnings / 0 errors；
`dotnet test Heartbeat.slnx --no-restore --configuration Debug` 为 943/943；CollectorProtocol 18/18，System
56/56，Hub 212/212。System deadline/restart 测试隔离连续运行 5/5；Browser 78/78 且 production build
成功。三轮独立复审最终无 P1/P2，建议 closeout。

### 2026-08-30 — closeout 复审重开

新的调度敏感证据证明 deadline 只包住返回后的 Task，不包住调用本身：
`applicationLifetime.CancelAsync()` 位于 deadline 外，`application.StopAsync(...).AsTask()` 与
InProcess `_collector.StopAsync(...)` 都可能在返回 Task 前同步阻塞。完整 solution 并行运行曾为
942/943，`DrainDeadlineStagesSystemIngressTailAndRestartReplaysDurableRemainder` expected 99、actual 98；
隔离串行 5/5 不能关闭该竞态。`SystemCollectorIngressStore` 还会保留坏 NDJSON 尾部，后续 append
导致下一次启动把坏行视为中间损坏。deadline 调用 fence、坏尾修复和真实跨进程 crash/restart/replay
smoke 完成前，本 issue 恢复为 `needs-triage`。

## Reopened acceptance

- [x] 所有用户/Collector 代码调用本身受硬 deadline/fence；到期后不能迟到 ACK 或突变 durable state。
- [x] 打开 ingress journal 时截断最后一行损坏尾部；append 后再次重启仍可恢复。
- [x] 真实跨进程 crash/restart/replay smoke 证明 durable remainder 可恢复，不以同进程 reopen 代替。

### 2026-08-30 — 同步调用 deadline fence 修复

先以失败测试证明三条同步阻塞路径均可让现实现超过 1 秒测试 fence：application lifetime
cancellation、application `StartAsync`/`StopAsync` 调用本身，以及 Hub 对 InProcess Collector
`StopAsync` 的调用。实现把不受信任调用调度到独立 Task，再用同一绝对 deadline 等待；deadline
callback 原子关闭 Protocol admission 并推进 delivery epoch，故迟到 Start/Stop 发布不能写 outbox、
迟到 ACK 不能删除 durable responsibility。Hub deadline 先关闭 writer session，再只观察迟返 Collector
与可选 deadline fence，replacement 不再被同步调用卡住。

验证：CollectorProtocol 21/21、Hub 213/213、System 56/56；新增 Protocol 同步阻塞组重复 10 次
30/30，Hub 同步 Stop 组重复 10 次 10/10；`git diff --check` 通过。坏尾修复与真实跨进程 smoke
仍未完成，因此 issue 保持 `needs-triage`。

### 2026-08-30 — ingress journal 坏尾修复

失败测试复现合法前缀后追加半行、Open 仅 `break`、继续 append 后二次启动丢失新 entry。Open 现在
按 UTF-8 byte offset 扫描 NDJSON；仅当最后一条记录发生 JSON 语法损坏时，以 write-through
`SetLength(lastValidOffset)` 截断并 fsync，随后 append 从合法边界继续。中间损坏仍抛 `JsonException`，
不会被尾部恢复规则掩盖。验证：System 58/58；坏尾修复与中间损坏拒绝重复 10 次 20/20；
`git diff --check` 通过。真实跨进程 smoke 仍未完成，issue 保持 `needs-triage`。

### 2026-08-30 — real cross-process crash/restart/replay smoke

先加入测试并证明当前因独立 crash harness 不存在而失败。新增的测试专用
`Heartbeat.Collector.System.CrashReplayHarness` 由 solution 与 System test project 显式构建，smoke
不再把同一进程中的 `Open` 当 restart：进程 A 以固定 FactId 持久化 final Segment、InputEvent 与满容量
拒绝对应的单条 Gap，flush 后 `Environment.FailFast` 硬退出；进程 B 从同一 NDJSON journal 启动，核对
两个固定 FactId 和 Gap loss count 后逐项 ACK；进程 C 再次启动，证明已 ACK remainder 不会复活。
每个子进程均有 15 秒硬上限，超时由父测试终止进程树。

验证：新 smoke red 1/1（harness 缺失）→ green 1/1；完整 System suite 59/59；green smoke 独立重复
10 次 10/10。该证据只证明当前 OS/filesystem 上的真实进程崩溃与重放，不外推为断电或三平台目录项
durability；跨平台 replacement contract 仍按 issue 02 的明确退出条件暂缓。issue 在最终并行全量验证与
双轴复审前保持 `needs-triage`。

### 2026-08-30 — ACK replacement 与 Hub deadline 线性化

完整 solution 的调度敏感失败在 `--no-build` 并发执行中稳定复现为 11/24：Stop 返回时 durable identity
union 仍为 100，但 50 ms 后 Protocol outbox 从 100 变为 99。根因不是 late Hub publish，而是 Hub 已经
返回 ACK 后，Collector 在锁外写 replacement 临时文件；Hub deadline 已 fence session 并返回，System
client 的 cancellation/fence 传播仍可能排队，迟到的最终 `File.Move` 因而删除一条 durable
responsibility。

修复先以确定性失败测试暂停 ACK replacement、让 deadline epoch 抢先，再证明 outbox 保留原 Fact。
Protocol outbox 的 delivery outcome 改为 copy-on-write：JSON 序列化与临时文件写入在提交门外，只有
最终原子 replacement 和内存 state publish 进入短临界区。InProcess binding 把该临界区接到 Hub
session 自有的 acknowledgement commit gate；Hub deadline 同步 fence 同一 gate，因此 ACK 抢先则
replacement 在 Stop 返回前完成，deadline 抢先则 replacement 不执行。该 gate 不调用 application/
Collector 代码，也不把平台 UI/window/input callback 接到磁盘 I/O。

验证：确定性 Protocol regression 1/1，Protocol suite 22/22，Hub deadline 相关 17/17；原 System
deadline/restart 测试在一次构建后的并发执行先复现 11/24 失败，修复后 24/24，再扩大到 50/50 全部
通过。最终 solution、Browser、cross-process smoke 与双轴复审尚未执行，因此 issue 继续保持
`needs-triage`。

### 2026-08-30 — Starting Collector 同步调用 deadline 修复

最终 Standards 复审发现 Starting Collector cleanup 仍在 deadline 外直接等待 lifetime
`CancelAsync`，且在 Collector 尚未完成 Initialize 时直接调用 `collector.StopAsync(...).AsTask()`；后者
可在返回 `ValueTask` 前同步阻塞。新增回归测试让 Initialize 忽略取消、Stop 调用本身同步阻塞，修复前
Runtime Dispose 超过 1 秒测试 fence。Starting cleanup 现在在入口即建立同一绝对 deadline，把 lifetime
cancellation 与 Stop 调用调度到隔离 Task，并分别以 deadline 等待；迟返任务只观察异常，不再占住
Runtime ownership。InProcess activation 的 deadline 判定也改为独立 timer task，collector cancellation
在 deadline 后异步传播，避免任意 cancellation callback 反向阻塞硬截止。

验证：Starting Collector deadline/failure 定向测试 3/3；Hub durable commit 的独立复审 finding 仍在
下一提交修复，因此 issue 保持 `needs-triage`。

### 2026-08-30 — Hub durable commit deadline fence

Spec/Standards 复审用阻塞 Event projection 证明 Collector-side outbox ACK fence 仍不足：Hub
`_commitFacts` 可持有 session gate 跨过 deadline，Stop 先返回，随后才写 Runtime state。回归测试现在在
Stop 返回时快照 Hub state，释放阻塞 projection 后要求文件不再变化；修复前稳定失败。Activation 新增
单一 delivery commit fence，Fact/Gap 的 `_store.Save + _state publish`、Collector outbox ACK replacement
与 Hub deadline 共用该线性化边界：deadline 抢先则 Hub commit 返回 retry/被 session fence 取消，commit
抢先则原子 store replacement 在 Stop 返回前完成。阻塞 projection 位于 commit gate 外，deadline 不等待
不受信任 sink；其迟返结果不能再改变 Hub durable responsibility。

验证：Hub Starting/deadline/commit gate 定向测试 4/4，System deadline/restart 回归 1/1；其余复审
findings 尚未关闭，issue 保持 `needs-triage`。

### 2026-08-30 — real Protocol cross-process crash/drain/restart smoke

最终 Spec 复审指出原 smoke 虽启动三个进程，但只直接调用 ingress store，未经过真实 Protocol、
Hub projection 和 drain result，无法证明 remainder 计数保真。替换后的 harness 在进程 A 启动真实
`CollectorRuntime` + `SystemInProcessCollector`，待 InputEvent 交付进入阻塞的 Hub projection 后
`Environment.FailFast`；进程 B 重启同一 runtime/data directory，经真实 Collector Protocol 重放
Segment、InputEvent 与后续 Gap，并要求 drain 精确为 `Drained / PendingFacts=0 /
PendingGaps=0 / RemainderDurable=true / Completed`；进程 C 再重启，证明已提交 Fact/Gap 仍可重放到
projection，但 ingress/outbox 的已 ACK remainder 不复活，第二次 drain 仍为 fully drained。

新 smoke 单轮 1/1，随后独立连续 10 轮、10/10。该证据覆盖当前 OS/filesystem 上的真实进程崩溃、
Protocol replay 与逻辑/完成 drain result；仍不外推为断电或三平台 directory-entry durability。

### 2026-08-30 — Event projection final commit deadline fence

第二轮独立复审发现 Hub 在 activation delivery fence 外调用同步 `IInputEventFactSink.Accept`；
生产 `InputEventBuffer` 可在 Stop 返回后才完成 cache replacement。确定性 red 测试让 sink 阻塞到
deadline，旧实现释放后记录到 2 次迟到 commit。新的强制 `ICollectorProjectionCommitFence`
契约允许 sink 在 fence 外准备，但必须通过 activation 自有 fence 提交最终持久突变；
deadline 抢先则 sink 返回 retry 且 cache 不变。`BeginDrain` 也改为不等待正在执行不受信
projection 的 session lock，使硬 deadline 能在 Stop 入口建立。System transcript red→green 1/1；
`InputEventBuffer` 拒绝 fence 后 restart 仍为空的生产 sink 测试 1/1。

### 2026-08-30 — Hub-owned fence、transient retry 与 final closeout

第四轮复审证明同步调用公开 `IInProcessCollectorDeadlineFence.FenceAfterDeadline()` 仍可绕过 deadline。
失败测试让该方法同步阻塞，旧实现 1 秒超时。最终设计由 Hub 在 activation initialize 时注入并持有
`ICollectorDurableCommitFence`；deadline 同步关闭的只有 Hub 自有 gate，Collector cancellation/fence
notification 均在隔离 worker 上 best-effort 执行，writer release 不再进入任意 Collector 代码。System
ingress journal 的最终 File.Move 使用同一 gate，故 late prepared mutation 无法成为权威状态。

同时修复 Runtime Fact prepare 的瞬时持久化失败：Fact/Gap Retry outcome 不进入 message replay cache，
storage 恢复后相同 messageId 可重新提交；System pump 的 unstaged prefix 在失败窗口计入 pending，持续
失败不能报告 fully drained。相关提交：`7bec280`、`268e4c4`、`1741eda`。

最终证据：Hub 219/219、System 73/73；Hub deadline stress 60/60，System deadline + ingress retry
stress 80/80；真实 Protocol 跨进程 crash/drain/restart 10/10；完整 solution 并行连续三轮 974/974；
build 0 warnings / 0 errors；Browser 78/78 + production build；第五轮 Spec 与 Standards 代码轴 P1/P2
清零。本 issue 状态更新为 `done`。可靠性声明只覆盖当前 OS/filesystem 的进程崩溃/restart，不外推为
断电或三平台 directory-entry durability。
