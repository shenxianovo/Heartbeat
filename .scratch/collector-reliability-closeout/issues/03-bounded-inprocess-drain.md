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
