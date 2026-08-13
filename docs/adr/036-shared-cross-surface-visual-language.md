# ADR-036: 跨表面共享视觉语言而非布局

## Status: Accepted

## Date: 2026-08-13

## Context

[ADR-033](./033-cross-platform-desktop-architecture.md) 确定 Windows 与 macOS 共用 Avalonia presentation，但没有规定桌面 Agent 与 Web Dashboard 应共享什么。完全照搬 Web 的响应式页面会弱化桌面信息架构；分别模仿 WinUI 与 AppKit 又会产生两套 UI，并使同一产品在三个表面失去一致性。

桌面端还需要同时容纳当前活动、Agent 状态、采集能力、采集器管理、设置与诊断信息。旧布局把这些内容平铺在单页中，主次不清，也没有统一的主题、图标、密度与交互状态规范。

## Decision

Heartbeat 的 Web Dashboard 与桌面 Agent 共享品牌 Token 和组件语言，包括天空蓝主色、语义状态色、字体层级、柔和表面与 Light/Dark/System 主题；两者不共享信息架构或页面布局。桌面端继续由 Windows 与 macOS 共用一套 Avalonia presentation，以紧凑左侧导航组织概览、采集器和设置，只保留窗口外壳、托盘或菜单栏等必要的平台差异。

桌面 UI 使用 Lucide 跨平台图标，品牌标题使用随应用分发的 Sora，正文使用平台系统字体。概览默认突出当前活动、Agent 状态和需要处理的异常；运行日志折叠为有界的诊断区域。能力存在但当前不可用时显示其状态，平台完全未实现的操作则隐藏。

文本配置采用显式保存，即时开关立即生效。页面内不提供重复的“隐藏”按钮，系统关闭按钮负责隐藏窗口而不停止 Agent。输入、导航、日志滚动与主题切换等行为由共享 presentation 定义，不在 platform head 各自分叉。

## Consequences

- ✅ Windows 与 macOS 获得同一套可测试的品牌化桌面 UI，不需要分别维护 WinUI/AppKit 视觉实现。
- ✅ Web 与桌面保持颜色、状态、字体和组件语义一致，同时允许各自采用合适的信息架构。
- ✅ 平台差异被限制在窗口生命周期、托盘或菜单栏、权限与更新等 platform-head seam。
- ⚠️ 共享主题中的控件状态必须同时验证 Windows 与 macOS，不能假设 Fluent 默认模板在两个后端完全一致。
- ⚠️ 品牌字体和图标库成为桌面分发资产，需要随包保留许可证并纳入构建验证。

## References

- [ADR-033](./033-cross-platform-desktop-architecture.md) — 共享 Avalonia presentation 与独立 platform head
- [`desktop/CONTEXT.md`](../../desktop/CONTEXT.md) — 桌面 Agent 与采集器页规范术语
- [`HeartbeatTheme.axaml`](../../desktop/Heartbeat.Desktop.UI/Themes/HeartbeatTheme.axaml) — 共享主题 Token 与控件样式
- [`MainWindow.axaml`](../../desktop/Heartbeat.Desktop.UI/Views/MainWindow.axaml) — 桌面信息架构与共享布局
