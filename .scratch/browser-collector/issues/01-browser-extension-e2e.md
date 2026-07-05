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
- 进行中的段用 close-and-reopen 模式周期性上报（服务端按 (Source, IdentityKey, 相邻) 续接，已实现，勿改）。
- 上报 `POST http://127.0.0.1:{port}/v1/segments`，body 为 `SegmentUploadRequest` 形状。hub 不可达或非 2xx 时指数退避，段在扩展本地缓冲不丢（浏览器重启后仍在）。
- 端口可在扩展选项页配置，默认与 Agent 的 `IngestPort` 默认值一致。

折叠逻辑写成纯函数（事件进、段出），配单元测试。

## Acceptance criteria

- [ ] Edge 加载扩展后正常浏览，服务端库中出现 `source='browser'` 的段，`GetSegmentsAsync` 可查到
- [ ] 同一页面停留跨多个上报周期，服务端续接为一条段（不碎裂）
- [ ] URL query/fragment 变化不切段（`watch?v=a` → `watch?v=b` 本片允许被合并，覆写表在 02 修正）
- [ ] 双窗口场景两条段并存，windowId 区分
- [ ] Agent 未运行时扩展不丢数据，Agent 恢复后补传
- [ ] 折叠纯函数有单元测试（切换、URL 变化、多窗口、close-and-reopen）
- [ ] `source='system'` 冒充被 hub 拒收的既有行为不受影响

## Blocked by

None - can start immediately
