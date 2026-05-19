# ADR-009: Velopack for Desktop Auto-Update

## Status: Accepted

## Date: 2026-05-19

## Context

需要为 WPF 客户端实现自动更新。候选方案：

1. **Squirrel.Windows** — 已停止维护，社区迁移至 Velopack。
2. **ClickOnce** — 老旧，部署体验差，不支持托盘应用场景。
3. **WiX + 自写 updater** — 复杂，需自行实现增量更新、回滚等。
4. **Velopack** — Squirrel 继任者，.NET 原生，支持 GitHub Releases 集成、增量更新、代码签名。

## Decision

选择 **Velopack**。原因：

- 活跃维护，Squirrel 官方推荐迁移目标
- 原生支持 GitHub Releases 作为更新源
- 增量更新减少下载量
- API 简洁，`VelopackApp.Build().Run()` 即可接管安装/卸载/更新生命周期

## Consequences

- ✅ 安装/更新/卸载生命周期由 Velopack 管理
- ✅ 增量更新，用户体验好
- ⚠️ 安装位置固定为 `%LocalAppData%`（见 [ADR-010](./010-per-user-localappdata-install.md)）
- ⚠️ 需在 Main 入口最早调用 `VelopackApp.Build().Run()`，影响启动流程
