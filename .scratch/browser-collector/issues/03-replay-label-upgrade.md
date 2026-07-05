# 03: 回放注意力线标签升级（ADR-019）

Status: done

## Parent

[PRD](../PRD.md)

## What to build

按 ADR-019 在 Dashboard 回放（活动时间线 + 应用详情的标题明细）实现标签升级：渲染某 system 段时，若存在同 App 的重叠 browser 段，标签从窗口标题升级为页面标题/URL；无重叠插件段的时间窗口（含扩展安装前的全部历史）fallback 到现有窗口标题明细，展示不装有。

- 前端按当前时间窗拉取插件段。服务端查询（`GetSegmentsAsync`，区间重叠语义）已存在；若 Dashboard 侧缺少对应认证端点/客户端方法则补齐，复用现有认证与 Device 过滤约定。
- 主视图仍是单一注意力线（system 前台段），**不建多轨播放器**——插件段只作为标签升级的原料，只在与前台段相交时参与渲染（悬空段不上屏，ADR-019 §2）。
- 升级后的标签展示页面级聚合（如按 IdentityKey 聚合页面停留），替代现有浏览器窗口标题的乱明细（"和另外 N 个页面"后缀问题在覆盖时段消失）。
- 不做真交集统计（ADR-017 §4 纪律）：升级只是显示效果。

## Acceptance criteria

- [x] 扩展覆盖时段：浏览器 App 的详情/时间线标签显示页面/URL 级信息，不再是窗口标题明细（用户实测，页面级行带圆点 + URL 副标签）
- [x] 扩展未覆盖时段（历史数据）：显示与现状完全一致（fallback 按时间窗口判定，不是按 App 全局判定；单测覆盖升级行与 fallback 行并存）
- [x] 非浏览器 App 的显示不受影响（无重叠插件段 → 纯 fallback 路径，单测覆盖与旧行为一致）
- [x] 时长数字仍来自 system 段（统计互斥轨不变，ADR-017 §4；单测断言时长取 system 段）
- [x] 前端有针对"重叠判定 + fallback 窗口"逻辑的测试（labelUpgrade.test.ts，8 例）

## Blocked by

- [01](./01-browser-extension-e2e.md)

## Comments

- 2026-07-05: 落地于 df6b64b。勘察发现 AppDetailModal 已有多轨回放 v1（即 ADR-019 的泳道展开态），故本片只做标题明细的标签升级；升级映射利用了"system 段按标题切段 ≈ 一段一页面"的结构性事实，按最大重叠择插件段，无需时长切割。附带 e5736f8：edge formatter 削除 " 和另外 N 个页面" 后缀（fallback 层永久服务扩展前历史）。
