# ADR-0012：Web 回放状态与 Track 查询独立管理

## 状态：已接受

## 日期：2026-09-18

## 背景

ADR-0011 已确定通用时间轴拥有时间几何，协议注册表拥有展示内容，但尚未明确页面选择状态、查询缓存与视图组合的责任。原实现把日期、来源、视窗、详情、Track 查询和密度缓存都放在 `ReplayWorkbench`；一个 Track 失败会让整个窗口失败，读取 QueryClient 快照又不订阅变化。

考虑过继续使用一个窗口级查询并在内部 `Promise.allSettled`，但它仍会让来源增减重取未变化 Track，也无法让缓存按 Track 独立失效和复用。

## 决策

- 页面选择状态由回放选择 hook 管理；日期、来源或视窗变化必须清理与旧选择有关的详情。
- TanStack Query 是服务端数据缓存的唯一权威。回放查询按 Owner、Track、时间范围和必要粒度独立建 key；视图组合层只合并当前成功结果，并显式保留失败 Track。
- 一条 Track 失败不隐藏其他 Track；只有所选 Track 全部无可用结果时才显示整个时间线失败。
- Point 密度细节仍按固定 tile 缓存。读取缓存必须订阅 QueryCache；已失效的密度层不参与绘制，刷新使当前时间窗内各 Track、各粒度的缓存统一失效。
- `ReplayWorkbench` 只组合过滤控件、状态组件和时间轴，不持有数据缓存或协议解释规则。

## 后果

- 来源增减可以复用未变化 Track 的结果，局部故障不会升级成整窗白屏。
- 查询 key 数量增加，但每条缓存有明确 Track 归属和生命周期。
- 刷新期间旧的细粒度密度不会压过新的概览；重新取得细节前只显示仍有效的数据。
- 真实 Collector 到 Web 的端到端链路仍未因此得到证明。

## 参考

- [`ADR-0011`](ADR-0011-web-timeline-presentation-ownership.md) — 时间轴与协议展示责任
- [`queries.ts`](../../src/Frontend/Heartbeat.Web/src/api/queries.ts) — Track 查询
- [`densityQueries.ts`](../../src/Frontend/Heartbeat.Web/src/api/densityQueries.ts) — 密度缓存订阅与失效
- [`useReplaySelection.ts`](../../src/Frontend/Heartbeat.Web/src/components/replay/useReplaySelection.ts) — 页面选择状态
- [`useReplayData.ts`](../../src/Frontend/Heartbeat.Web/src/components/replay/useReplayData.ts) — 查询结果与视图组合
