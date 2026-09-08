# 03 — 恢复应用详情标题明细中的浏览器网址

Status: done

## Parent

[PRD](../PRD.md)

## Problem

2026-09-08 owner 报告 Analytics Dashboard 从应用排行点进浏览器后，标题明细不显示网址。
真实 Chrome 详情的回放 tooltip 证明 Segment.attributes 保存的是完整 Fact payload，外层键为
`identityKey/title/attributes`，URL 位于 payload.attributes.url；前端 `urlOf` 仍仅读顶层 url。

## Resolution

初次修复在 Dashboard DTO 适配层兼容完整 payload 与历史直接 attributes 两种形状。
随后 owner 选择 [Analytics 原生 Fact](../../native-analytics-facts/PRD.md)：历史形状在无损导入时归一，
Dashboard 改为只读结构化 `payload.attributes.url`，不再猜测字符串包装层。原始历史仍归档保留。

## Acceptance

- [x] 用线上观察到的完整 payload 结构稳定复现“有标题、无网址”，测试先失败。
- [x] 应用详情显示完整原始 URL，包括 query 与 fragment。
- [x] 直接保存 attributes 的历史段仍能显示 URL。
- [x] 缺失、无效、非字符串属性不伪造 URL；读取 canonical payload 内的 attributes.url。
- [x] 前端类型检查、测试、构建和本地真实数据 UI 验证通过。

## Verification

- `npm test -- src/components/AppDetailModal.test.ts` 修复前 1 failed / 7 passed；
  失败用例渲染 `Example page` 却没有完整 URL，旧格式对照用例通过。
- `npm run verify` → typecheck 通过、40 files / 270 tests passed、production build 通过。
- 用当前 Vite 前端连接本地 Analytics 数据，打开 Chrome 应用详情，标题明细已显示页面 URL；
  未执行线上部署。本次未 commit。

- 原生 Fact 读接口调整后：前端 40 files / 271 tests、typecheck 与 build 通过；native 与
  legacy-import 组件夹具共享同一结构化 Payload。先前本地真实数据 UI 验证是初次修复证据，
  新迁移的真实数据库演练仍由 native-analytics-facts issue 跟踪，未宣称已完成生产迁移。
