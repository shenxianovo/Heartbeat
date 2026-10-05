# ADR-0005：从最小 macOS Collector 演进到完整桌面观察

## 状态：已接受

## 日期：2026-09-12

[`3ca4aed`](https://github.com/shenxianovo/heartbeat/commit/3ca4aed9d149318f679aa9891f8ff0ff03a9fdb2) — feat(recording): add replay queries and independent macOS collection

## 背景

记录链路需要先由真实桌面数据验证，但旧实现同时包含 Hub runtime、包管理、权限、窗口、输入和旧观察模型。整体迁移会把尚未确认的抽象带入重写。采样也不能等待网络，否则慢上传会造成观测空白。

## 决策

先实现独立命令行 macOS Collector，只采集前台应用。采样与上传分离，待上传 Record 保存在内存；持续读数复用 Record ID 并延长区间，失败重试不阻塞采样。此阶段不引入本地持久队列、设备表或安装身份。

Target 暂作为协议 `device_id`，不代表 Device Identity 已实现。

## 后果

- 最小链路验证了 Collector 可以使用四层记录模型。
- 采样不受上传延迟阻塞，旧回执不能清除更新后的进度。
- Hub 接管前的内存数据可能在进程退出时丢失。

## 演进：2026-09-14

完整桌面范围确认后，最小实现扩展为当前五条 Track：

- NSWorkspace 提供应用及锁屏、会话、显示器和系统休眠通知；
- Accessibility 提供窗口焦点与标题；
- Input Monitoring 提供非文本物理输入；
- 各能力状态独立记录和恢复。

事件与周期快照串行投影；状态变化会淘汰过期快照。连续时间使用包含系统休眠的单调时钟。Away 原因可以重叠，全部恢复后才开始新的前台区间。默认最大确认间隔从采样间隔两倍调整为三倍，使一次晚到的 tick 不被误判为断采。

本演进仍不增加 Hub 接管前的持久队列、输入文本、浏览器 URL 或 Device Identity 注册。窗口与应用拆分的后续决策见 [ADR-0007](ADR-0007-foreground-window-is-its-own-observation.md)。

## 参考

- [Collector 运行与局部约束](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md)
- [桌面协议](../protocols/)
- [Hub 交付](../hub-record-delivery.md)
- [未决设计](../recording-open-questions.md)
