# Browser Collector IdentityKey refinement

Status: ready-for-human

## Current state

Browser Collector 已通过 ExternalHost Binding 接入 Collector Protocol。稳定 FactId、Revision、ACK/重试、持久 outbox、Stream Gap、per-App Collector Instance Desired State，以及跨平台 Collector page 均已实现；回放标签升级也已完成。

现行运行时、身份和管理语义以 [ADR-040](../../docs/adr/040-collector-runtime-and-protocol-foundation.md) 与 [`collection/CONTEXT.md`](../../collection/CONTEXT.md) 为准。

## Implemented behavior

Browser IdentityKey 默认使用 `origin + pathname`，通过显式 hosts/path/params 规则表保留页面身份参数。
YouTube `/watch` 保留 `v`，切换视频生成新段；追踪参数和 fragment 不制造新段。完整原始 URL 继续保存在
Fact payload 中。随 [Analytics 原生 Fact](../native-analytics-facts/PRD.md) 迁移，历史形状由导入层归一，
Dashboard 标题明细只读取结构化 `payload.attributes.url`。

## Outcome

- 增加数据驱动的 per-domain query 参数保留规则，首个规则覆盖 YouTube `/watch` 的 `v`。
- 默认规则继续丢弃无身份意义的 query 与 fragment。
- 完整原始 URL 始终保存在 Fact Payload 的 attributes 中。
- 规范化保持纯函数，并覆盖默认、覆写和边界行为测试。

## Issue

- [02 — IdentityKey 规范化覆写表](issues/02-identitykey-override-table.md)
- [03 — 应用详情浏览器网址](issues/03-app-detail-page-url.md)

## Lifecycle debt restored by repository scan

- [01 — Browser extension 真实 E2E](issues/01-browser-extension-e2e.md)

这张旧 issue 在 `d69735b` 中以非 terminal 状态被删除，但当前叙述又把对应能力写成已实现。
2026-08-30 扫描按 lifecycle 规则恢复原文；在用当前代码/真实 UI 逐项补齐证据并置 `done`，或明确
裁决为 `wontfix` 之前，本 PRD 不再只宣称剩余 IdentityKey 一项。

2026-09-08：02/03 已完成并记录验证证据；01 仍有稳定续段与断线补传两项真实 E2E 门禁，
因此 PRD 为 `ready-for-human`，不能置 `done`。Collector 安装、连接与卸载验收继续在 Package Registry PRD 跟踪。

## Comments

- 2026-09-08：owner 明确裁决删除旧 WPF 相关待办，已移除旧 issue 04（WPF 插件管理页），
  按 `wontfix` 收口，不再恢复为待办。当前 Desktop Collector Marketplace 的实现与验收继续由
  [Collector Package Registry issue 10](../collector-package-registry/issues/10-desktop-collector-marketplace.md) 跟踪。
