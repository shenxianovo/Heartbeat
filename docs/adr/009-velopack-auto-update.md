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
- ⚠️ 增量更新有两个前提，缺一则退化为全量下载（2026-07 曾同时缺失）：
  1. CI 打包前必须 `vpk download` 上一版本作为 diff 基线，否则 delta 包根本不会生成；
  2. 发布不能用 single-file——delta 按 nupkg 内文件逐个 diff，单个大 EXE 任何改动都接近全量，非 single-file 下未变的程序集（runtime DLL、确定性编译的未改项目）delta 为零。本项目分发由 Velopack 接管、安装目录用户不可见，single-file 无收益，故弃用。
- ⚠️ 客户端 `Velopack` 包与 CI 的 `vpk` CLI 必须锁定同一版本（当前 1.2.0），升级时两处同步。
