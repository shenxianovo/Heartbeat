# ADR-0007：前台应用与前台窗口是两个观测对象

## 状态：已接受

## 日期：2026-09-16

## 背景

前台应用 Record 曾把窗口标题和进程 ID 计入身份，并把原生通知直接当作切段信号。结果是同一应用被切成大量相邻短块，标题权限失败还会连带中断应用区间。

展示端合并会掩盖采集数据错误；旧实现的“最近点击”门控又用输入活动猜测标题变化是否重要，无法形成稳定协议。因此需要在采集模型中拆开两个观测对象。

## 决策

前台应用与前台窗口是两个观测对象，各自成一条 Track：`desktop.application.foreground` v1 只回答「前台是哪个应用」，新增的 `desktop.window.foreground` v1 只回答「前台窗口叫什么」。

- 读数不变且确认未中断时，持续延长同一 Record；原生通知只触发重新读数。
- 应用身份只含平台、标识种类和原生标识，不含进程 ID。
- `window_title` 失败只结束窗口 Record；`application` 失败同时结束应用和窗口。订阅尚未完成时不报告可用或失败。
- capability 与 input 事件不使采样快照失效。活动变化淘汰快照时立即重新采样。
- 默认确认上限为采样间隔三倍，连续两次缺失确认才视为中断。

不恢复 main 的点击门控。标题变化就是窗口观测的变化，不需要输入活动来批准。

## 后果

- 同一应用不再因窗口变化产生相邻碎块。
- 标题权限和故障只影响窗口 Track。
- 消费者需要读取两条 Track 才能同时得到应用和窗口。
- 窗口标题可能含敏感内容，由读侧控制可见性；采集端不静默丢弃。
- 应用 Track 不区分同一应用的不同进程实例。

## 参考

- [`docs/protocols/desktop-application-foreground-v1.md`](../protocols/desktop-application-foreground-v1.md) — 前台应用协议。
- [`docs/protocols/desktop-window-foreground-v1.md`](../protocols/desktop-window-foreground-v1.md) — 前台窗口协议。
- [`src/Collectors/Heartbeat.Collector.Desktop/DesktopRecordProjector.cs`](../../src/Collectors/Heartbeat.Collector.Desktop/DesktopRecordProjector.cs) — 两个观测对象的区间投影。
- [`src/Collectors/Heartbeat.Collector.Desktop.Mac/MacSystemObservationSource.cs`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/MacSystemObservationSource.cs) — 能力状态与标题读取。
- [`ADR-0005`](ADR-0005-minimal-macos-desktop-collector.md) — 最小 macOS Collector 的起点范围。
- [领域语言](../../CONTEXT.md) — Foreground Application Observation 与 Foreground Window Observation。
