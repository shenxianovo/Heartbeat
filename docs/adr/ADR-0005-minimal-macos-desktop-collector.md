# ADR-0005：最小 macOS 桌面 Collector 直接写入记录接口

## 状态：已接受

## 日期：2026-09-12

[`3ca4aed`](https://github.com/shenxianovo/heartbeat/commit/3ca4aed9d149318f679aa9891f8ff0ff03a9fdb2) — feat(recording): add replay queries and independent macOS collection

## 背景

新的记录链路已经支持 Collector 注册、Track 获取、前台应用协议、批量上传和 Track 级重放查询。下一步需要让桌面端真实产生 `desktop.application.foreground` v1 Record，验证四层模型可以被 Collector 消费。

旧桌面实现包含 Hub runtime、Collector 包管理、权限状态、窗口标题、输入事件和旧观察模型。若在重写早期直接迁移这些能力，会把尚未重新确认的运行时抽象带入新模型，也会延后最小链路的验证。

旧实现的采集与上传本就独立。若在采样循环中等待网络请求，慢上传会造成观测空白；用户确认新实现继续保持二者独立，并先使用内存保管待上传记录。

## 决策

先新增一个独立命令行 macOS Collector。它启动后用配置提供的 Target 注册 `heartbeat.collector.desktop.macos`，解析 `desktop.application.foreground` v1 Track，然后轮询 `NSWorkspace.frontmostApplication`，把当前前台应用作为明确区间 Record 上传。

Collector 优先使用 macOS Bundle ID 作为平台原生应用标识；缺失时退回 executable path。同一应用连续观测时复用同一 Record ID 并延长结束时间；应用变化或无法读取前台应用时断开连续性并等待下一次有效观测创建新 Record。

采样只更新内存中的待上传记录，上传独立执行。同一 Record 合并最新完整区间，应用切换产生的其他 Record 保留。成功回执只确认其发送时的快照，不能清除之后产生的新进度。常驻运行时上传失败保留重试；一次性模式等待一次上传结果并报告失败。断采阈值仍需另行确认。

本阶段不引入本地持久上传队列、不接旧 Hub runtime、不增加设备表或安装身份。Target 暂时作为当前协议的 `device_id`，只是最小 Collector 的载荷传递方式；设备配对和跨重装恢复仍按 ADR-0003 留到后续设计。

## 后果

- ✅ 桌面 Collector 可以直接走新的三步写入链路，验证后端记录模型。
- ✅ Collector 侧模块很小，前台应用读取、连续区间合并和 HTTP 写入各自集中。
- ✅ 不把旧运行时、设备模型或分轨字段提前带入 Clean Room 重写。
- ✅ 上传延迟不会阻塞采样，旧回执不会清除更新的进度。
- ⚠️ 内存缓冲没有持久 outbox，Collector 退出可能丢失未确认进度；长期离线时不同 Record 的积压会增加内存用量。
- ⚠️ 当前只支持 macOS 前台应用，不记录窗口标题、URL、输入事件或应用显示名。

## 参考

- [`CONTEXT.md`](../../CONTEXT.md) — Collector、Target、Track 与 Device Identity 的领域定义。
- [`docs/protocols/desktop-application-foreground-v1.md`](../protocols/desktop-application-foreground-v1.md) — 桌面前台应用协议。
- [`src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md) — 最小 Collector 运行方式。
- [`docs/recording-open-questions.md`](../recording-open-questions.md) — 持久队列、断采规则和设备关联的未决问题。
- [`src/Collectors/Heartbeat.Collector.Desktop.Mac`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac) — macOS Collector 实现。
- [`docs/adr/ADR-0003-device-identity-across-reinstallation.md`](ADR-0003-device-identity-across-reinstallation.md) — 设备身份恢复边界。

## 演进：2026-09-14（恢复系统观察能力）

Clean Room 已确认完整桌面范围，因此最小轮询实现被一个当前实现替换：NSWorkspace 负责应用激活及锁屏、会话、显示器和系统休眠通知；Accessibility 负责窗口焦点与标题；Input Monitoring 负责非文本物理输入。应用、Away Signal、输入和观察状态分别进入四条 Record Track，共用一个 Collector 内存缓冲和 Hub 批量交接。

应用/窗口/标题通知直接建立新区间，不用点击确认。四类 Away 原因独立计数并可重叠；全部恢复后才重新开始应用 Record。明确离开、能力失败和超过最大确认间隔会断开应用连续性，恢复后即使值相同也使用新 ID。最大间隔可配置，当前默认是采样间隔两倍。

> 注记（2026-09-17）：实现取值是采样间隔的三倍，见 [`CollectorOptions.cs`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/CollectorOptions.cs) 的 `maximum-gap-seconds` 默认值，与本决策当时写的两倍不同。原因是两倍之下一次晚到的 tick 就够判成断采，而那属于正常调度抖动；改成三倍后要连续两次缺失确认才判定观察中断。这个放宽由 [`ADR-0007`](ADR-0007-foreground-window-is-its-own-observation.md) 决策第 4 条落地，本决策原文不改。

Accessibility 与 Input Monitoring 独立降级；权限缺失或观察器失败写入历史 observation status，其他能力继续工作，权限恢复后进程自行重试。状态 available 不构成完整性承诺。

本演进仍不加入 Hub 前持久队列、容量治理、浏览器 URL、输入文本或设备身份注册。ADR-0006 接受的通用结果更正机制也不在此实现；当前持续 Record 仍只按 ADR-0002 单调续期。

验收修复落实了上述时间与交接边界：原生事件在 Session 接收时取时间，与周期快照串行投影，采样期间若状态变化则丢弃过期快照；UTC 基准使用包含系统休眠的 macOS 单调经过时间推进。内存缓冲仅在锁内复制快照，批量分组与计量在锁外进行。验证证据与真机边界见 [system 验收记录](../validation/system-acceptance.md)。
