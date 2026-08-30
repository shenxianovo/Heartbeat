# Browser Collector IdentityKey refinement

Status: needs-triage

## Current state

Browser Collector 已通过 ExternalHost Binding 接入 Collector Protocol。稳定 FactId、Revision、ACK/重试、持久 outbox、Stream Gap、per-App Collector Instance Desired State，以及跨平台 Collector page 均已实现；回放标签升级也已完成。

现行运行时、身份和管理语义以 [ADR-040](../../docs/adr/040-collector-runtime-and-protocol-foundation.md) 与 [`collection/CONTEXT.md`](../../collection/CONTEXT.md) 为准。

## Remaining problem

默认 Browser IdentityKey 使用 `origin + pathname`，丢弃 query 与 fragment。这可以消除追踪参数造成的假碎片，但会把 query 承载页面身份的站点过度合并。例如 `youtube.com/watch?v=a` 与 `youtube.com/watch?v=b` 当前得到同一 IdentityKey，SPA 切换视频时可能不切段，旧段最终只保留最新 URL 与标题。

## Outcome

- 增加数据驱动的 per-domain query 参数保留规则，首个规则覆盖 YouTube `/watch` 的 `v`。
- 默认规则继续丢弃无身份意义的 query 与 fragment。
- 完整原始 URL 始终保存在 Attributes 中。
- 规范化保持纯函数，并覆盖默认、覆写和边界行为测试。

## Issue

- [02 — IdentityKey 规范化覆写表](issues/02-identitykey-override-table.md)

## Lifecycle debt restored by repository scan

- [01 — Browser extension 真实 E2E](issues/01-browser-extension-e2e.md)
- [04 — Desktop Collector 管理入口](issues/04-wpf-collector-management.md)

这两张旧 issue 在 `d69735b` 中以非 terminal 状态被删除，但当前叙述又把对应能力写成已实现。
2026-08-30 扫描按 lifecycle 规则恢复原文；在用当前代码/真实 UI 逐项补齐证据并置 `done`，或明确
裁决为 `wontfix` 之前，本 PRD 不再只宣称剩余 IdentityKey 一项。
