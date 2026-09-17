# ADR-0008：窗口标题要站稳才承认

## 状态：已接受

## 日期：2026-09-16

## 背景

[ADR-0007](ADR-0007-foreground-window-is-its-own-observation.md) 将窗口拆成独立观测后，终端 spinner、进度文本、滚动字幕和浏览器导航中间态会产生大量短 Record。

先考虑过按文本相似度判等（编辑距离小于阈值就算同一个标题），否掉了：

- 文本距离不能可靠表示语义是否相同；
- 相似关系不可传递，会让固定 Record value 沿比较链漂移；
- 每个应用都需要不同阈值。

于是改用变化的**时间形态**：真实的标题变更之后标题会停下来，噪声不会。参数不能靠猜，所以先量。

## 决策

窗口标题的变化要先站稳 `window-title-dwell`（默认 **1.5 秒**）才承认为新的观测值：

- **站不住的候选被前一个区间吸收**，不产生 Record，也不留空洞。
- **承认时区间从候选第一次出现的那一刻算起**，不是从承认那一刻算起。静置只推迟写入，不改区间。
- **应用切换那一刻的标题立即承认**，不等静置——那是另一个窗口，没有可等的余地。
- **中断发生时（标题读不到、Away Signal、能力失败）当前候选若已站够时间就补记成 Record**，不把一段真实存在过的标题连同中断一起丢掉；确认间隔已经超限时不补记，因为那段时间根本没有观测。
- 前台应用 Track 完全不受影响：静置只作用于窗口标题。

参数可配（`--window-title-dwell-ms` / `HEARTBEAT_COLLECTOR_WINDOW_TITLE_DWELL_MS`）。设为 0 等于关掉静置，每次标题变化都承认。

## 1.5 秒是怎么来的

当时因缺少 Accessibility 权限，使用本机约 46.6 小时的历史投影数据。终端 spinner 最慢一帧为 1.05 秒，其他标题变化有明显长尾，因此选择高于噪声上限的 1.5 秒。模拟将 4370 个观测值收敛为 3620 条 Record，标题累计陈旧时间占总时长 0.28%。

这份数据无法判断原生通知是否漏送，仍需有 Accessibility 权限的探针复核。

## 代价

- 新标题最晚在下一次采样后写出，但区间仍从首次出现开始。
- 被吸收的中间态会让前一区间最多多出 1.5 秒。
- 单一阈值适用于所有应用，其他机器和应用可能需要重新测量。
- 中断后的第一个标题立即承认，可能保留一条短噪声 Record。

## 实现

- [`DesktopRecordProjector`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/DesktopRecordProjector.cs) — `ObserveWindowTitle` 与 `SettlePendingTitle`。
- [`CollectorOptions`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/CollectorOptions.cs) — 静置时长的取值与校验。
- [`desktop.window.foreground` v1](../protocols/desktop-window-foreground-v1.md) — 区间规则。
- [`heartbeat-dev probe window-title`](../verification.md#现场探针) — 量抖动、模拟候选参数。
