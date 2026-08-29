# Browser Collector IdentityKey refinement

Status: ready-for-agent

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
