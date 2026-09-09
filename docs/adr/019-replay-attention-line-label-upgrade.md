# ADR-019: Replay 主视图 — 注意力线 + 标签升级，泳道为展开态

## Status: Proposed

## Date: 2026-07-05

(pending implementation)

## 2026-09-09：新活动视图的适用边界

Owner 为“当天经历”确认独立路由 `/u/:username/experience`，采用 Subject 泳道，
与原 Dashboard 并存。本 ADR 的单一注意力线与标签升级继续描述原页面；新页面不以
重叠程度择一替换 System 标签，而是并列展示同 Subject、同 AppIdentity 且时间重叠的
Browser 候选。Account 等独立观测直接可见，短 Segment 保留身份和边界，通过缩放、
平移与逐条详情阅读，不合并事实。具体范围见[当天经历 PRD](../../.scratch/daily-experience-projection/PRD.md)。

## Context

ADR-017 §4 将回放描述为 "multi-track overlay (one track per source, plugin tracks nested under their AppId's track)"。落到真实数据上，按 Source 分轨有两处歧义：

1. **悬空段问题。** 插件段在其 App 失焦时仍然存活（tab 活跃 ≠ 浏览器前台，这正是 ADR-017 的双时间线公理）。独立的插件轨必须回答"用户在 VSCode 时，browser 轨那条还画不画"。
2. **互斥段挤轨。** 同一 Source 内依次活跃的段（tab 切换）本质是又一条互斥时间线，塞进一条轨与 system 轨并排，视觉上是两条平行的"注意力线"，用户无法一眼判断哪条是真的。

同时，回放的产品定位已经收窄（CONTEXT-MAP / glossary）：Recap 是入口、Replay 是深挖、拒绝电影化。Replay 用户的心智模型是**回忆"那段时间我的注意力去哪了"**，不是并排分析多个观测者。

## Decision

1. **主视图 = 注意力线。** 单一时间线，跟随 system 前台段（唯一互斥轨，时长可求和）。
2. **标签升级。** 渲染某 system 段时，若存在同 AppId 的重叠插件段，段标签由窗口标题升级为插件语义（URL/页面/文件路径）。这是"看起来像交集"的显示效果，**不做真交集计算**——延续 ADR-017 §4 的 overlay-first、intersection-later。
3. **Fallback 按时间窗口判定。** 无重叠插件段的时段——包括插件安装之前的全部历史——照旧显示窗口标题明细（ADR-015/016 层）。"是否被插件覆盖"不是 App 的静态属性，是每个时间窗口的事实。数据没有，展示不装有。
4. **泳道视图（每 App 一条泳道，Source 为泳道内图层）是展开态**，服务"并排对比"的分析场景，需求真实出现后再建。

## Consequences

- ✅ 浏览器扩展价值的大头（语义标签替换乱标题）以最小前端改动兑现；标题噪声（ADR-016 的治理对象）对已覆盖应用在主视图自然消失。
- ✅ 悬空插件段在主视图无需回答——插件段只在与前台段相交时参与渲染。
- ✅ 实现成本：现有单时间线回放页 + 一条"重叠段标签替换"规则；无需先建多轨播放器。
- ⚠️ 主视图隐藏"tab 在后台仍活跃"的事实（如后台放音乐）。泳道展开态负责暴露；在此之前该信息只入库不上屏。
- ⚠️ ADR-017 §4 中 "one track per source" 的渲染表述由本 ADR 细化取代（§4 的统计边界不受影响）。

## References

- [ADR-015](./015-window-title-segment-dimension.md) / [ADR-016](./016-title-noise-control.md) — fallback 层
- [ADR-017](./017-activity-segment-pluggable-collectors.md) — 数据模型与双时间线公理；本 ADR 细化其渲染表述
