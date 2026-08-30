# Collector 数据可靠性收口

Status: needs-triage

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

- [ ] 既有 [strict segment ingest issue](../repository-understanding/issues/05-strict-segment-ingest-outcomes.md)
- [ ] [01 — 在摄入边界前旋转连续 Segment](issues/01-rotate-continuous-segments.md)
- [ ] [02 — durable InputEvent 满容量不再静默丢失](issues/02-durable-input-capacity.md)
- [ ] [03 — 让 InProcess drain 受 deadline 约束](issues/03-bounded-inprocess-drain.md)

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

## Non-goals

- 不改变 24h server policy 的安全边界；Collector 通过有界 chunk 遵守它。
- 不借机实现 Package Registry、host self-update 或新的 Analytics facts。
- 不对没有现场证据的旧 outbox 承诺迁移；迁移只覆盖当前真实格式。
