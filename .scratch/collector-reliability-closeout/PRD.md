# Collector 数据可靠性收口

Status: done

## Problem

当前大改动已经建立 durable Fact/Upload/Protocol，但扫描发现四个会把“已接收/已停止”说得比真实情况
更强的 P1 缺口：server strict ingest 仍可能 `skip + 200`；System/VRChat 的长时连续段没有在 24h
摄入边界前旋转；durable InputEvent 满容量时静默丢最旧；InProcess drain 在创建 deadline token 前用
`CancellationToken.None` 停应用，可能永远到不了 truthful drain result。

这些是 Collector Package Registry rollout 的前置 gate：不能先扩大独立发布能力，再把数据丢失或
停机悬挂带给更多版本。

## Outcome

- HTTP ACK 只承诺服务端真实摄入；永久拒绝可由 UploadStream 二分并进入 dead-letter。
- 任意长期不变的 System/VRChat observation 会形成有界、连续、无重叠的 finalized chunks。
- durable InputEvent 容量耗尽要么 backpressure，要么产生可重放的 Stream Gap，绝不静默 trim。
- 所有 Execution Driver 按其真实边界完成 drain：InProcess 到期 fence、ManagedProcess 到期 terminate、
  ExternalHost revoke lease，并准确报告或明确标记 unknown/non-durable remainder。

## Required work

- [x] 既有 [strict segment ingest issue](../repository-understanding/issues/05-strict-segment-ingest-outcomes.md)
- [x] [01 — 在摄入边界前旋转连续 Segment](issues/01-rotate-continuous-segments.md)
- [x] [02 — durable InputEvent 满容量不再静默丢失](issues/02-durable-input-capacity.md)
- [x] [03 — 让 InProcess drain 受 deadline 约束](issues/03-bounded-inprocess-drain.md)

全部四项 done 后，本 PRD 才能标 `done`，Registry rollout 才解除可靠性 gate。

推荐的并行 lane、汇合 gate 与后续 Registry 依赖见
[Collector 独立交付实现路线图](../../docs/architecture/collector-delivery-implementation-roadmap.md)。

## Closeout

四项 required work 已分别提交并通过自动验证；最终 A4 全量验证为解决方案 build 0 warnings / 0 errors、
.NET 943/943、Browser 78/78 与 production build 成功。三轮独立复审最终无 P1/P2。Feature A reliability
gate 已关闭；本 PRD 不包含 Package Registry 实现或 rollout。

### 2026-08-30 — reliability closeout 复审重开

上一轮 `done` 结论被新的可复现证据推翻，保留原 closeout 仅作为历史记录。四张 required issue
均恢复为 `needs-triage`：同步阻塞调用可绕过 drain deadline；System Event 拒绝与 Stream Gap
记录之间存在崩溃窗口且平台回调同步 fsync；rotation continuation 尚无 durable identity；ingress
坏尾未修复；strict ingest 的事务编排仍泄漏在 Controller。另有 VRChat checkpoint 兼容退出台账、
atomic JSON replacement 漂移及真实跨进程 crash/restart/replay smoke 未完成。所有 P1/P2 与重复并行
验证、双轴复审关闭前，本 PRD 不得恢复 `done`，Registry reliability gate 继续关闭。

### 2026-08-30 — compatibility friction ledger 补齐

复审发现 Browser pending Gap 缺 `gapId`、Collector Protocol outbox schema v1 point Gap、Hub Runtime
state v2 empty `GapId` 三条兼容读取仍只有“下一版删除”式注释，没有可执行退出证据。
`docs/architecture/compatibility-debt.md` 现分别记录真实服务对象、Collection 子域 owner、受支持
Profile/data-directory/state 盘点归零、明确离线/回滚窗口与必保留 fixture。当前没有现场盘点
和窗口证据，因此只关闭“无退出台账”的 P2，不删除分支，也不新增兼容行为。

### 2026-08-30 — reopen 后最终 closeout

上一轮 943/943 与“三轮复审通过”只保留为历史证据；本轮重开后的最终功能 HEAD 重新完成：solution
build 0 warnings / 0 errors；`dotnet test Heartbeat.slnx --no-restore --no-build --configuration Debug`
在项目并行执行下连续三轮均为 974/974；Browser 78/78、production build 与 Collector contract check
通过；真实 Protocol 跨进程 crash/drain/restart smoke 连续 10/10；Hub deadline stress 60/60，System
deadline + ingress retry stress 80/80。第五轮独立 Spec 复审与 Standards 代码轴均确认 P1/P2 清零；
Standards 指出的 tracker 漂移由本 lifecycle commit 关闭。

最终边界由 Hub-owned durable commit fence 负责：deadline 后不再同步进入 Collector；System ingress
以 32 KiB target 的 history-bounded COW chunks、100-event atomic capacity batch、ACK tombstone/reset
与 unstaged retry prefix 保证失败重试和 truthful drain。不可拆的 oversized rotation record 独占 chunk，
不裁剪真实 payload；reset 后旧 chunk 删除只是逻辑不可达数据的 best-effort 物理清理，不属于 deadline
后的权威状态突变。Local Data Smoke 使用现有外部 `.env.local` 完成 check → baseline → verify：Segment
watermark 从 `2026-08-30T11:33:47.271Z` 推进到 `2026-08-30T11:43:47.279Z`，quality signals 全零；
该证据不外推为一次新的 window-switch 或全平台断电 durability 验收。

## Non-goals

- 不改变 24h server policy 的安全边界；Collector 通过有界 chunk 遵守它。
- 不借机实现 Package Registry、host self-update 或新的 Analytics facts。
- 不对没有现场证据的旧 outbox 承诺迁移；迁移只覆盖当前真实格式。

### 2026-08-31 — 第二轮独立复审重开

新的可复现证据推翻上一轮 `done` 结论：VRChat checkpoint 在时钟回拨时可持久化无效空 Gap；
System ingress 会把完整但语义不兼容的末条记录误当 torn tail 截断；strict ingest 的空 batch 拒绝仍
停留在 Controller；System rollover 的 deferred observation 可被 gate 释放后的新 callback 超车；
Starting Collector 在 Initialize 未返回时缺少 terminal Hub-owned durable fence；Protocol transcript
的非 timeout 行为测试还共享过短 startup budget。所有六项严格 TDD、完整验证与双轴独立复审完成前，
本 PRD 保持 `needs-triage`，Registry reliability gate 继续关闭。

### 2026-08-31 — ManagedProcess transcript 非 timeout 启动预算

回归先固定非 timeout transcript 必须有 30 秒调度余量，当前共享 Options 的 5 秒预算稳定 0/1；修复仅
集中提升测试 fixture 的非 timeout startup budget，不改生产默认值与 Runtime 超时语义。授权暂停/恢复
的 1s/2s 测试及专门 `StartupTimeout_ProducesStructuredFailure` 的 250ms 预算保持原样；非法 hello 仍只
接受 `protocol_invalid_message`，不把 `activation_start_timeout` 当成可接受结果。预算 contract 1/1，
非法 hello + 专门 timeout 4/4，非法 hello 隔离 20 轮共 60/60，完整 ManagedProcess transcript 27/27。
最终并行 solution 三轮门禁前 PRD 保持 `needs-triage`。

### 2026-08-31 — Starting Collector terminal durable fence

Starting Collector 现从资源暴露前即持有 Hub-owned session fence；Runtime Dispose/deadline 与正常 terminal
Stop 都先 fence，再释放 ownership。System 把同一 fence 贯通到 Protocol 初始 outbox publication 与
ingress tail repair；outbox corruption recovery 保留原 authoritative 证据直到 recovery Gap 的 fenced
replacement 成功。确定性 red/green 覆盖 Initialize/Stop 迟返、prepared outbox fence、corrupt recovery
中间 fence、两种 ingress repair 与 cooperative stop。相关完整套件为 Hub 222/222、System 78/78、
Protocol 26/26；最终门禁与双轴复审前 PRD 继续保持 `needs-triage`。

### 2026-08-31 — 第二轮 final lifecycle closeout

六项重开问题均以稳定 red 回归锁定后修复：VRChat clock rollback 不再伪造空 Gap；ingress 只修复可证明
为 torn/un-terminated 的末条；空 batch 在 application contract、任何实体副作用前拒绝；System rollover
通过单一有序 handoff 防止新 callback 超车；Starting Collector 与 Protocol 初始 outbox/repair 从资源暴露
起受同一 Hub-owned terminal fence 约束；非 timeout transcript 使用独立 30 秒测试预算而不改变生产超时。

最终门禁在测试夹具 TOCTOU 修复后从零重新计数：`dotnet build Heartbeat.slnx --no-restore
--configuration Debug` 为 0 warnings / 0 errors；Browser 78/78 且 production build 成功；collector contract
check 通过；真实 Protocol cross-process crash/drain/restart 连续 10/10；Hub/System/Protocol 定向压力分别
60/60、80/80、40/40，native callback 协调夹具 20/20；`dotnet test Heartbeat.slnx --no-restore
--no-build --configuration Debug` 在项目并行执行下连续三轮 989/989；`git diff --check` 通过。

固定点 `8d0c63a4e7f48e0b126eba05eca460ce404c646f` 的独立双轴复审结论：Spec 无 finding，P1/P2/P3
均为 0；Standards 无 hard violation，P1/P2 为 0，保留两项不阻塞 P3 judgement call（terminal fence
命名与多 bool test double）。四张 required issue 均恢复 `done`，本 PRD 亦恢复 `done`，Registry
reliability gate 的本轮前置条件关闭。

可靠性声明仍只覆盖当前 OS/filesystem 下已验证的进程 crash/restart、协议 replay 与 logical drain；不
外推为 power-loss、跨平台 directory-entry/fsync durability 或 dead-letter 双文件事务原子性。VRChat
checkpoint v1、Protocol schema v1 point Gap / 缺失 DeliveryOrder、Runtime v2 空 GapId 的读取兼容分支
继续受 `docs/architecture/compatibility-debt.md` 的 inventory、迁移、rollback/离线窗口退出条件约束。

### 2026-08-31 — background delivery handoff 竞态重开

新的确定性协调回归推翻上一轮 `done`：drain 已推进 client delivery epoch、但 application cancellation
任务尚未调度时，旧 background Fact/Gap ACK 恢复会把 handoff 的取消误记为 `flush_cancelled`；随后
final flush 虽清空 durable outbox，logical reason 仍不收敛。新增 Fact/Gap 两 case 在修复前稳定为
0/2，精确结果是 pending facts/gaps 均为 0、completion `completed`、reason `flush_cancelled`。
本轮完整验证与 lifecycle closeout 完成前，Registry reliability gate 重新关闭。

### 2026-08-31 — background delivery handoff final lifecycle closeout

Client 现在在推进 delivery epoch 前先发布 background→drain handoff marker；handoff 后旧 background
delivery 的取消只结束旧 pump ownership，停止 ingress 后的有界 final flush 仍独立决定真实 logical
outcome。没有放宽 `IsFullyDrained`，也没有按 pending 0 泛化覆盖 deadline、stop、persistence 或
completion failure。Fact/Gap 确定性协调回归在旧 catch 下精确为 0/2（pending 0/0、completion
`completed`、reason `flush_cancelled`），修复后为 2/2，并重复 40/40；原 deadline restart replay
重复 30/30。

本轮最终门禁：solution build 0 warnings / 0 errors；Protocol 28/28；Browser 78/78 且 production build
成功；collector contract、IDE1006 style 与 diff check 通过；真实 cross-process crash/drain/restart
10/10；Hub/System/Protocol terminal/deadline 压力分别 60/60、80/80、40/40；solution 项目并行执行按
12 个项目 summary 从零计数，连续三轮均为 991/991。独立 Spec 与 Standards 复审均为 P1/P2/P3 0。

本轮没有修改采集、摄入、投影或持久化 mutation，Local Data Smoke 不适用。可靠性声明仍只覆盖当前
OS/filesystem 下已验证的进程 crash/restart、Protocol replay 与 logical drain；不外推 power-loss、
跨平台 directory-entry/fsync durability 或 dead-letter 双文件事务原子性。既有兼容读取继续受
`docs/architecture/compatibility-debt.md` 的 inventory、迁移与 rollback/离线窗口退出条件约束。
