# ADR-037: 以能力与可执行边界组织 Collection 项目

## Status: Accepted

## Date: 2026-08-14

## Context

跨平台拆分完成后，`desktop/` 同时包含可复用 hub、system Collector、平台 adapter、共享 UI、Updater 与 platform head。九个生产项目虽然无循环，却让目录范围、`Core` 后缀和已漂移的 `Agent` 项目名掩盖了真实职责。未来无头场景只需要 server 旁的 hub，不需要在桌面设备上保留无 UI Agent host。

## Decision

Collection 上下文统一位于 `collection/`，按 `hub/`、`desktop/`、`collectors/` 分区；结构目录使用 lowercase，具体项目目录使用 .NET PascalCase。命名分三条轴：`Heartbeat.Collection.*` 表示上下文共享运行时，`Heartbeat.Collector.*` 表示产生某一类事实的 Collector，`Heartbeat.Desktop.*` 表示桌面交付能力或平台。

程序集只用于独立 executable 或需要编译器强制执行的依赖防火墙。`Heartbeat.Collection.Hub` 与 `Heartbeat.Collector.System` 保持纯 .NET，禁止依赖 Avalonia、Velopack 或平台 API；Windows/macOS adapter 分别并入独立 platform head，旧 console Runner 退役；Velopack 保持为独立的 `Heartbeat.Desktop.Updater.Velopack` adapter，避免供应商依赖进入 UI 与共享能力。每个 .NET 生产项目的测试项目与其相邻且一一对应；非 .NET Collector 保留各自生态的测试布局。

## Consequences

- `Agent` 保留为运行中的后台采集引擎这一领域词，不再作为含义不对称的程序集名。
- system Collector 的正式项目名为 `Heartbeat.Collector.System`；`system` 仍是内置且不可停用的 Source。
- platform head 仍独立隔离签名、权限、生命周期与原生依赖，保留 ADR-033 的核心边界。
- 本 ADR supersedes ADR-005 的可复用 Windows Agent 类库与 console Runner 决策，并取代 ADR-033 中的旧程序集命名；无头 hub 的星形拓扑与复用边界保持不变。
