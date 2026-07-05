# PRD: Browser Collector — 第一个真实采集器

来源：2026-07-05 grilling 会话（ADR-019 + CONTEXT-MAP / shared / desktop CONTEXT.md 同日更新，commit 0383520）。

## 目标

把浏览器从"窗口标题最贫瘠的应用"变成语义最丰富的一条轨：MV3 扩展采集 active tab 活动，经 loopback hub 汇入既有管线；回放主视图按 ADR-019 做标签升级；WPF 提供采集器管理。

## 范围内

1. MV3 浏览器扩展（`collectors/browser/`，仓库新顶层目录）
2. 回放注意力线标签升级（ADR-019 前端实现）
3. WPF 插件管理页（Active = 流量推断，Deactivate = hub 黑名单 403）

## 范围外

- Recap（意义层）——显式推迟至本期落地后（见 shared/CONTEXT.md Recap 词条）
- 泳道多轨展开态（ADR-019 §4）
- Collector SDK / 模板——从第一、二个插件的共性中提炼（ADR-017 §5）
- 交集统计（ADR-017 §4 overlay-first 纪律）

## 已就位的基础设施（勿重做）

- hub ingest：`SegmentIngestWorker`（`POST 127.0.0.1:{IngestPort}/v1/segments`）+ `SegmentIngestService` 缓冲
- 上传管线：`SegmentUploadService` → 服务端 `SegmentController` → `UsageService.SaveSegmentsAsync`（UUIDv7 幂等 + (Source, IdentityKey) 续接）
- 查询：`UsageService.GetSegmentsAsync`（区间重叠语义，ADR-018）

## 关键决策（详见 ADR/CONTEXT）

- IdentityKey = origin+pathname，掐 query/fragment；per-domain 覆写表处理"query 即身份"站点；完整 URL 存 Attributes（判据可有损，原始数据无损）
- 扩展忠实记录 tab 活跃，不管窗口是否前台；多窗口时每窗口各记其 active tab（windowId 进 Attributes）
- 主视图 = 注意力线 + 标签升级；fallback 按时间窗口判定（ADR-019）
- Active/Deactivate 语义见 desktop/CONTEXT.md

## Issues

| # | Title | Type | Blocked by |
|---|-------|------|-----------|
| 01 | 浏览器扩展端到端走通 | AFK | — |
| 02 | IdentityKey 规范化覆写表 | AFK | 01 |
| 03 | 回放注意力线标签升级 | AFK | 01 |
| 04 | WPF 插件管理页 | AFK | 01 |

建议顺序：01 → 03（价值兑现点）→ 02 / 04。
