# System Collector 能力对照

盘点日期：2026-09-14。旧实现固定到 `main` 的 `86911e75459b0038eeba8aec623d2f3d2890a1f3`；重写基线为 `0bcd3b0`。下表描述代码中的能力，不将代码存在视为真机验收通过。

本轮既定范围是恢复 **macOS 采集到展示链路**。Windows 仅对照盘点，暂不实现。恢复能力不要求恢复 main 的点击门控、滚轮阈值、Runtime、包管理或缓存兼容路径。

## 能力清单

| 能力 | main macOS | main Windows | 当前 macOS 重写 |
| --- | --- | --- | --- |
| 前台应用 | NSWorkspace 应用激活，平台身份及显示名 | WinEvent 前台变化，平台应用身份 | NSWorkspace 通知与周期确认；Bundle ID 优先，缺失时 executable path；保存显示名 |
| 窗口与标题 | Accessibility 焦点窗口/标题通知 | 前台、最小化及窗口名称 WinEvent | 恢复焦点窗口/标题通知；窗口标题自成 `desktop.window.foreground` Track，与前台应用分开记录 |
| 标题转场规则 | 共用活动模型：同应用标题变化要求最近 1 秒内点击；窗口变化是否无条件切段受配置影响 | 同左 | 标题变化只切窗口 Record，不切应用 Record，因而不需要点击门控；标题读取失败只降级窗口观测；不采集页面 URL；不保存持久窗口身份 |
| Away Signal | 锁屏、会话失活、显示器休眠、系统休眠；原因集合防止部分恢复误判整体恢复 | 显示关闭/开启、挂起/恢复通知；adapter 直接转发，没有 macOS 同样的原因集合；共用模型另可按配置的应用名归为 away | 四种原因各自形成 Range，可重叠；所有原因解除后才创建新应用区间；无输入不推断离开 |
| 物理按键 | key-down/key-up tap，物理位置映射；共用缓冲过滤长按重复；原 tap 未订阅修饰键 flagsChanged | 低层键盘 hook，扫描码/扩展位映射；共用重复过滤 | 普通按键及 flagsChanged 修饰键；只保存去重 key-down，不保存文本或 key-up；左右修饰键按各自设备位判断 |
| CapsLock | 存在映射，但原 tap 的 flagsChanged 缺口使映射不能证明实际采到 | 经 Windows 物理键路径 | 仅使用物理 stateless 位；锁存 on/off 本身不证明物理按下；设备实际 flags 仍需真机验证 |
| 鼠标与滚轮 | 左/右/中键；滚轮转换后累计每 ±120 保存一档事件 | 左/右/中键；垂直滚轮经相同 ±120 累计 | 保存鼠标按钮按下、水平/垂直滚动的原生方向和增量，保留 line/point 单位，不按固定阈值计数 |
| 权限与故障 | Accessibility、Input Monitoring 独立状态与恢复；标题、交互信号、输入录制独立配置 | hook 可用性和采集配置；不能等同 macOS TCC 权限模型 | 独立退化，终端提示权限缺失；保存历史状态；AX 真正读取失败与正常无值区分，订阅实际附着且读取成功才确认恢复 |
| 区间与交接 | 共用 System 模型与 AppMonitor，30 秒快照；ingress、SDK outbox、Runtime 及旧格式兼容 | 同一共用实现 | 五 Track 共用内存缓冲与 Hub 批量交接；临时断采阈值默认采样间隔三倍；恢复不跨未知空白续接；Hub 前持久化及完整保留保证不在本轮范围 |

## 证据入口

旧代码可用 `git show 86911e75459b0038eeba8aec623d2f3d2890a1f3:<path>` 读取，无需切换工作树：

- `collection/desktop/Heartbeat.Collector.System/README.md`、`Observations/SystemActivityModel.cs`、`Collection/AppMonitorService.cs`、`Input/InputEventBuffer.cs`：共用规则、交付及旧实现的验收边界。
- `collection/desktop/Heartbeat.Desktop.Mac/Observations/MacDesktopObservationSource.cs`、`MacAccessibilityEvents.cs`，以及 `Input/MacInputEventCollector.cs`、`Native/MacInputNativeEventTranslator.cs`：macOS 平台能力。
- `collection/desktop/Heartbeat.Desktop.Windows/Utils/WindowsDesktopObservationSource.cs`、`WindowsWindowEventMonitor.cs`、`WindowsPowerMonitor.cs`、`LowLevelInputHook.cs`，以及 `Services/InputEventCollector.cs`：Windows 平台能力。未据显示/挂起信号推断 Windows 已有独立锁屏/会话通知。

当前代码见 [Collector README](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md)、[系统观察源](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/MacSystemObservationSource.cs)、[原生 adapter](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/Native/)、[Record 投影](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/DesktopRecordProjector.cs)。数据含义见 [application](../protocols/desktop-application-foreground-v1.md)、[window](../protocols/desktop-window-foreground-v1.md)、[away](../protocols/desktop-system-away-v1.md)、[input](../protocols/desktop-input-event-v1.md)、[status](../protocols/desktop-observation-status-v1.md) 协议。

## 验证边界

本轮本地自动回归 .NET 237、Vitest 15、Playwright 9 项通过；这些结果覆盖可控观察、投影、交接及查询展示，不证明所有平台实际回调或数据完整。另已完成真实 Collector → 临时 Hub 持久接管和正常重启冒烟，结果见 [验收记录](system-acceptance.md)。

尚需常驻真机核对应用/窗口/标题变化、锁屏和休眠及重叠恢复、左右修饰键与 CapsLock、鼠标和双向滚动，以及权限撤销/授予后的独立退化与恢复，再读回对应时间位置和展示。`--once` 不验证这些行为；模拟事件、时钟与权限 fixture 也不能代替它们。main 自身 README 同样保留了两平台原生权限和真实回调的人工验收边界。
