# ADR-033: 以 Avalonia 与平台适配器构建跨平台桌面 Agent

## Status: Accepted

## Date: 2026-08-11

## Context

当前 `Heartbeat.Agent` 同时包含可移植 hub 管线、桌面 system 采集器状态机与 Win32 adapter，并整体锁定 `net10.0-windows`；桌面 UI 又由 WPF 承载。新增 macOS 支持时，如果继续在同一程序集条件编译或长期维护 WPF 与另一套 UI，平台权限、签名、更新与生命周期会污染共享核心。

## Decision

Heartbeat 将桌面端拆为纯 .NET 的 `Hub.Core`（摄入、缓存、上传、鉴权等 hub 运行时）与 `Desktop.Core`（system 采集器及平台无关状态机），Windows 和 macOS 分别提供窗口、输入、电源、图标、机器身份及自启动 adapter。桌面 UI 迁移到共享的 Avalonia UI 库，由 Windows/macOS 独立 platform head 启动和打包；每个平台保持 UI 与 Agent 同进程，关闭窗口只隐藏并继续在托盘或菜单栏运行。

选择独立 platform head 而不是单一多目标宿主，是为了隔离权限、签名、公证、更新和原生依赖；选择一次性替换 WPF 而不是长期并存，是为了避免维护两套 UI 与生命周期。无头 hub 只依赖 `Hub.Core`，不携带桌面采集概念。

macOS 的 Accessibility 与 Input Monitoring 权限按能力启用时分别请求，不在首次启动批量索取；权限缺失或被拒绝时 Agent 继续以较浅观测深度采集，并在 UI 中明确显示降级状态。

macOS platform head 以菜单栏 accessory app 运行，不常驻 Dock；用户从菜单栏打开共享 Avalonia 设置窗口。窗口关闭只隐藏 UI，不停止同进程 Agent。

## Consequences

- ✅ hub、桌面状态机与平台 API 的依赖方向清晰，无头 hub 不携带桌面概念。
- ✅ Windows 与 macOS 共用 Avalonia UI 和领域状态机，同时保留独立签名、权限与发布入口。
- ✅ macOS 在权限不足时可以按能力降级，而不是停止整个 Agent。
- ⚠️ WPF 宿主需要一次性退役；平台托盘、通知、单实例和更新入口需要分别适配。

## References

- [ADR-005](./005-extract-agent-library.md) — 现有 host-agnostic、但仍绑定 Windows 的 Agent 边界
- [ADR-032](./032-device-as-observed-subject.md) — `Hub.Core` 抽取与可复用 hub 拓扑
- [ADR-016](./016-title-noise-control.md) — 跨平台窗口事件与权限降级
- [ADR-011](./011-github-releases-update-source.md) — 桌面发布与更新来源
