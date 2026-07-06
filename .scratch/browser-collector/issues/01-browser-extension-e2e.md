# 01: 浏览器扩展端到端走通（tracer bullet）

Status: ready-for-agent

## Parent

[PRD](../PRD.md)

## What to build

在新顶层目录 `collectors/browser/` 建一个 Manifest V3 浏览器扩展（Chromium 系，Edge 验收），把"当前哪个 tab 活跃"折叠成 ActivitySegment，推送到 Agent 的 loopback ingest hub，走通 扩展 → hub → 上传 → 服务端入库 的完整链路。

行为：

- 监听 active tab 的身份变化（tab 切换、当前 tab 的 URL 变化），把连续的"同一活动"折叠成 `[StartTime, EndTime]` 段。折叠在扩展侧完成（ADR-017 §3：collectors fold）。
- **忠实记录，不判前台**：浏览器窗口是否在操作系统前台与扩展无关（双时间线公理，ADR-017）。多窗口时每个窗口各记其 active tab，两段并存合法，`windowId` 进 Attributes。
- 段形状：`source='browser'`；`IdentityKey` = origin + pathname（掐 query/fragment，本片不做覆写表——那是 issue 02）；`Title` = 页面标题；`AppName` = 浏览器进程名（如 `msedge.exe`，作关联提示）；`Attributes` = `{url(完整原始 URL), domain, windowId}`；`Id` = 扩展生成的 UUIDv7。
- 进行中的段用稳定 Id 快照上报（ADR-018）：同一活动持有同一 UUIDv7，每个上报周期推送 EndTime 单调生长的快照，服务端按 Id upsert 收敛（已实现，勿改）。单段生长超过服务端 MaxDuration（24h）前扩展侧应轮换新 Id，防快照被校验丢弃。
- 上报 `POST http://127.0.0.1:{port}/v1/segments`，body 为 `SegmentUploadRequest` 形状。hub 不可达或非 2xx 时指数退避，段在扩展本地缓冲不丢（浏览器重启后仍在）。
- 端口可在扩展选项页配置，默认与 Agent 的 `IngestPort` 默认值一致。

折叠逻辑写成纯函数（事件进、段出），配单元测试。

## Acceptance criteria

- [x] Edge 加载扩展后正常浏览，服务端库中出现 `source='browser'` 的段，`GetSegmentsAsync` 可查到（2026-07-05 用户实测链路通）
- [ ] 同一页面停留跨多个上报周期，服务端续接为一条段（不碎裂）
- [x] URL query/fragment 变化不切段（单测覆盖；`watch?v=` 过度合并留待 02 覆写表）
- [x] 双窗口场景两条段并存，windowId 区分（单测覆盖）
- [ ] Agent 未运行时扩展不丢数据，Agent 恢复后补传（实现：storage.local 队列 + 指数退避，待实测）
- [x] 折叠纯函数有单元测试（切换、URL 变化、多窗口、快照生长、24h 轮换）
- [x] `source='system'` 冒充被 hub 拒收的既有行为不受影响（hub 接收侧未改动）

## Comments

- 2026-07-05: 01-A 落地于 575a380（骨架 + 折叠 + 上报），01-B 落地于 bda86cf（选项页 + 退避）。相对原文的两处实现决策：(1) 快照机制按 ADR-018 稳定 Id upsert 而非 close-and-reopen；(2) 发现服务端 MaxDuration=24h 校验会静默丢弃超长快照，fold 增加 23h 自动轮换。

## Blocked by

None - can start immediately
